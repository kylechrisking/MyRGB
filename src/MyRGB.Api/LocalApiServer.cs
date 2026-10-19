using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyRGB.Core;

namespace MyRGB.Api;

/// <summary>
/// A small HTTP API on localhost so other local tools (the Stream Deck plugin, scripts, hotkeys)
/// can drive the lights. Built on HttpListener so the app needs only the base .NET runtime.
/// Bound to "localhost" only: nothing is reachable from the network.
/// </summary>
/// <remarks>
/// GET  /api/state                      power, brightness, active preset
/// GET  /api/presets                    every preset with a color swatch
/// GET  /api/devices                    connected devices
/// POST /api/presets/{id}/activate
/// POST /api/presets/next | /previous
/// POST /api/power        {"on": true}  empty body toggles
/// POST /api/brightness   {"value": 0.8} or {"delta": -0.1}
/// GET  /api/events                     server-sent events, one "state" event per change
/// POST /api/app/show                   bring the desktop window to the front
/// Every POST must carry an "X-MyRGB" header (any value).
/// </remarks>
public sealed class LocalApiServer : IAsyncDisposable
{
    public const int DefaultPort = 6743;

    /// <summary>Required on every POST (any value).</summary>
    public const string ClientHeader = "X-MyRGB";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILightingController _lights;
    private readonly Action? _showWindow;
    private readonly ILogger _log;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private Task _loop = Task.CompletedTask;

    /// <param name="showWindow">Called by POST /api/app/show; a second app instance uses it to surface the first.</param>
    public LocalApiServer(ILightingController lights, int port = DefaultPort, ILogger? log = null, Action? showWindow = null)
    {
        _lights = lights;
        _showWindow = showWindow;
        _log = log ?? NullLogger.Instance;
        Port = port;
        // "localhost" prefixes need no URL ACL, so this works without admin.
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }

    public int Port { get; }
    public string BaseUrl => $"http://localhost:{Port}/api";

    public Task StartAsync(CancellationToken ct = default)
    {
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) when (_cts.IsCancellationRequested || !_listener.IsListening) { return; }
            catch (HttpListenerException ex) { _log.LogWarning(ex, "API accept failed"); continue; }

            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            string path = req.Url!.AbsolutePath.TrimEnd('/');
            string method = req.HttpMethod;

            if (method == "GET" && path == "/api/events")
            {
                await StreamEventsAsync(res);
                return;
            }

            // A custom header can't be sent cross-site without a CORS preflight (which we never approve),
            // so web pages open in a browser can't drive the lights. Local tools just add the header.
            if (method != "GET" && req.Headers[ClientHeader] is null)
            {
                await WriteJson(res, 403, new { error = $"Send the '{ClientHeader}' header with POST requests." });
                return;
            }

            object? result = (method, path) switch
            {
                ("GET", "/api/state") => _lights.State,
                ("GET", "/api/presets") => _lights.GetPresets(),
                ("GET", "/api/devices") => _lights.GetDevices(),
                ("POST", "/api/presets/next") => Do(() => _lights.StepPreset(1)),
                ("POST", "/api/presets/previous") => Do(() => _lights.StepPreset(-1)),
                ("POST", "/api/power") => Power(await ReadBody<PowerRequest>(req)),
                ("POST", "/api/brightness") => Brightness(await ReadBody<BrightnessRequest>(req)),
                ("POST", "/api/app/show") => Do(() => _showWindow?.Invoke()),
                ("POST", _) when path.StartsWith("/api/presets/") && path.EndsWith("/activate") =>
                    Activate(Uri.UnescapeDataString(path["/api/presets/".Length..^"/activate".Length])),
                _ => NotFound.Instance,
            };

            if (result is NotFound nf)
                await WriteJson(res, 404, new { error = nf.Message ?? $"No route {method} {path}" });
            else
                await WriteJson(res, 200, result);
        }
        catch (JsonException ex)
        {
            await WriteJson(res, 400, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "API request {Method} {Path} failed", req.HttpMethod, req.Url?.AbsolutePath);
            try { await WriteJson(res, 500, new { error = ex.Message }); } catch { /* client gone */ }
        }
    }

    private LightingState Do(Action action)
    {
        action();
        return _lights.State;
    }

    private object Activate(string id) =>
        _lights.ActivatePreset(id) ? _lights.State : new NotFound($"No preset '{id}'.");

    private LightingState Power(PowerRequest? body)
    {
        _lights.SetEnabled(body?.On ?? !_lights.State.Enabled);
        return _lights.State;
    }

    private LightingState Brightness(BrightnessRequest? body)
    {
        double target = body?.Value ?? _lights.State.Brightness + (body?.Delta ?? 0);
        _lights.SetBrightness(Math.Clamp(target, 0, 1));
        return _lights.State;
    }

    private async Task StreamEventsAsync(HttpListenerResponse res)
    {
        res.ContentType = "text/event-stream";
        res.Headers["Cache-Control"] = "no-cache";
        res.SendChunked = true;
        var changed = new SemaphoreSlim(0);
        void OnChange(object? s, EventArgs e) => changed.Release();
        _lights.StateChanged += OnChange;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var bytes = Encoding.UTF8.GetBytes($"event: state\ndata: {JsonSerializer.Serialize(_lights.State, Json)}\n\n");
                await res.OutputStream.WriteAsync(bytes, _cts.Token);
                await res.OutputStream.FlushAsync(_cts.Token);
                // Wake on change, or every 15 s as a keep-alive.
                await changed.WaitAsync(TimeSpan.FromSeconds(15), _cts.Token);
                while (changed.CurrentCount > 0) changed.Wait(0);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or IOException) { /* client left */ }
        finally
        {
            _lights.StateChanged -= OnChange;
            try { res.Close(); } catch { }
        }
    }

    private static async Task<T?> ReadBody<T>(HttpListenerRequest req) where T : class
    {
        if (!req.HasEntityBody) return null;
        using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
        var text = await reader.ReadToEndAsync();
        return string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<T>(text, Json);
    }

    private static async Task WriteJson(HttpListenerResponse res, int status, object? body)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body, Json);
        res.StatusCode = status;
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    public sealed record PowerRequest(bool? On);
    public sealed record BrightnessRequest(double? Value, double? Delta);

    private sealed record NotFound(string? Message)
    {
        public static readonly NotFound Instance = new((string?)null);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch (ObjectDisposedException) { }
        try { await _loop; } catch { }
        _cts.Dispose();
    }
}
