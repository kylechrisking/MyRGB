using System.Net.Http.Json;
using System.Text.Json;

namespace MyRGB.StreamDeck;

internal sealed record LightingState(bool Enabled, double Brightness, string? ActivePresetId, string? ActivePresetName);

internal sealed record PresetSummary(string Id, string Name, string EffectId, bool BuiltIn, List<string> Swatch);

/// <summary>Talks to the MyRGB desktop app's local API and follows its state over server-sent events.</summary>
internal sealed class MyRgbClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http = new() { BaseAddress = new Uri("http://localhost:6743/api/"), Timeout = TimeSpan.FromSeconds(3) };
    private readonly HttpClient _events = new() { BaseAddress = new Uri("http://localhost:6743/api/"), Timeout = Timeout.InfiniteTimeSpan };

    public MyRgbClient()
    {
        _http.DefaultRequestHeaders.Add("X-MyRGB", "streamdeck");
    }

    public LightingState? State { get; private set; }
    public bool Online => State is not null;

    /// <summary>Raised with the latest state, or null when the app goes offline.</summary>
    public event Func<LightingState?, Task>? StateChanged;

    /// <summary>Fetch the state now instead of waiting for the event stream (used when a key first appears).</summary>
    public async Task RefreshAsync()
    {
        try { await Publish(await _http.GetFromJsonAsync<LightingState>("state", Json)); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { await Publish(null); }
    }

    public async Task<List<PresetSummary>> GetPresetsAsync() =>
        await _http.GetFromJsonAsync<List<PresetSummary>>("presets", Json) ?? [];

    public Task<bool> ActivateAsync(string presetId) => PostAsync($"presets/{Uri.EscapeDataString(presetId)}/activate", null);
    public Task<bool> StepAsync(int delta) => PostAsync(delta >= 0 ? "presets/next" : "presets/previous", null);
    public Task<bool> TogglePowerAsync() => PostAsync("power", null);
    public Task<bool> SetPowerAsync(bool on) => PostAsync("power", new { on });
    public Task<bool> SetBrightnessAsync(double value) => PostAsync("brightness", new { value });
    public Task<bool> AdjustBrightnessAsync(double delta) => PostAsync("brightness", new { delta });

    private async Task<bool> PostAsync(string path, object? body)
    {
        try
        {
            using var res = body is null ? await _http.PostAsync(path, null) : await _http.PostAsJsonAsync(path, body, Json);
            if (!res.IsSuccessStatusCode) return false;
            await Publish(await res.Content.ReadFromJsonAsync<LightingState>(Json));
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            await Publish(null);
            return false;
        }
    }

    /// <summary>Follow /api/events forever, reconnecting every few seconds while the app is closed.</summary>
    public async Task FollowAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var stream = await _events.GetStreamAsync("events", ct);
                using var reader = new StreamReader(stream);
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    if (line.StartsWith("data: ", StringComparison.Ordinal))
                        await Publish(JsonSerializer.Deserialize<LightingState>(line[6..], Json));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException) { }

            await Publish(null);
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }

    private async Task Publish(LightingState? state)
    {
        if (state == State) return;
        State = state;
        if (StateChanged is { } handler) await handler(state);
    }

    public void Dispose()
    {
        _http.Dispose();
        _events.Dispose();
    }
}
