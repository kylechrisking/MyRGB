using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyRGB.Core.Games;

namespace MyRGB.Api;

/// <summary>
/// Takes the posts Counter-Strike 2 and Dota 2 send through Game State Integration and hands them to
/// <see cref="GameFeed"/>. Bound to "localhost" only, and every post must carry the token MyRGB wrote
/// into the game's config file. Only runs while Game lighting is on.
/// </summary>
public sealed class GameStateListener : IAsyncDisposable
{
    public const int DefaultPort = 6744;

    private const int MaxBody = 64 * 1024;

    private readonly string _token;
    private readonly ILogger _log;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private Task _loop = Task.CompletedTask;

    public GameStateListener(string token, int port = DefaultPort, ILogger? log = null)
    {
        _token = token;
        _log = log ?? NullLogger.Instance;
        Port = port;
        // "localhost" prefixes need no URL ACL, so this works without admin.
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }

    public int Port { get; }

    /// <summary>Raised on a listener thread after each accepted post.</summary>
    public event Action<GameState>? Received;

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync, CancellationToken.None);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) when (_cts.IsCancellationRequested || !_listener.IsListening) { return; }
            catch (HttpListenerException ex) { _log.LogWarning(ex, "Game listener accept failed"); continue; }

            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        try
        {
            var req = ctx.Request;
            if (req.HttpMethod != "POST" || req.ContentLength64 > MaxBody)
            {
                res.StatusCode = 400;
                return;
            }

            string body = await ReadAsync(req);
            if (GsiParser.Parse(body, _token) is { } state)
            {
                GameFeed.Update(state);
                Received?.Invoke(state);
                res.StatusCode = 200;
            }
            else res.StatusCode = 400; // not a game we know, or the wrong token
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Game post failed");
            res.StatusCode = 500;
        }
        finally
        {
            try { res.Close(); } catch { /* the game hung up */ }
        }
    }

    private static async Task<string> ReadAsync(HttpListenerRequest req)
    {
        using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
        var buffer = new char[MaxBody + 1];
        int read = 0, n;
        while (read < buffer.Length && (n = await reader.ReadAsync(buffer.AsMemory(read))) > 0) read += n;
        return read > MaxBody ? "" : new string(buffer, 0, read);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch (ObjectDisposedException) { }
        try { await _loop; } catch { }
        _cts.Dispose();
        GameFeed.Clear();
    }
}
