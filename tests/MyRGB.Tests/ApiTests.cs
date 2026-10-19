using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MyRGB.Api;
using MyRGB.Core;
using MyRGB.Core.Devices;
using MyRGB.Core.Effects;
using MyRGB.Core.Engine;
using MyRGB.Core.Layout;
using MyRGB.Core.Presets;

namespace MyRGB.Tests;

public sealed class ApiTests : IAsyncLifetime
{
    private const int Port = 6799;
    private readonly LightingEngine _engine;
    private readonly LightingService _lights;
    private readonly LocalApiServer _server;
    private readonly HttpClient _http = new() { BaseAddress = new Uri($"http://localhost:{Port}/api/") };

    public ApiTests()
    {
        var devices = new DeviceManager();
        var effects = EffectRegistry.CreateDefault();
        _engine = new LightingEngine(devices, effects, new CanvasLayout());
        _lights = new LightingService(_engine, new PresetLibrary(), effects, devices);
        _server = new LocalApiServer(_lights, Port);
        _http.DefaultRequestHeaders.Add(LocalApiServer.ClientHeader, "tests");
    }

    public async ValueTask InitializeAsync() => await _server.StartAsync();

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        _engine.Dispose();
        _http.Dispose();
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Activates_steps_and_reports_presets()
    {
        var ct = TestContext.Current.CancellationToken;
        var res = await _http.PostAsync("presets/fire/activate", null, ct);
        res.EnsureSuccessStatusCode();
        Assert.Equal("fire", _lights.State.ActivePresetId);

        var state = await (await _http.PostAsync("presets/next", null, ct)).Content.ReadFromJsonAsync<LightingState>(Json, ct);
        Assert.Equal("starfield", state!.ActivePresetId);

        var presets = await _http.GetFromJsonAsync<List<PresetSummary>>("presets", Json, ct);
        Assert.Contains(presets!, p => p.Id == "ocean" && p.Swatch.Count == 5);

        Assert.Equal(HttpStatusCode.NotFound, (await _http.PostAsync("presets/missing/activate", null, ct)).StatusCode);
    }

    [Fact]
    public async Task Power_and_brightness()
    {
        var ct = TestContext.Current.CancellationToken;
        await _http.PostAsync("power", null, ct); // empty body toggles
        Assert.False(_lights.State.Enabled);
        await _http.PostAsJsonAsync("power", new { on = true }, ct);
        Assert.True(_lights.State.Enabled);

        await _http.PostAsJsonAsync("brightness", new { value = 0.4 }, ct);
        await _http.PostAsJsonAsync("brightness", new { delta = -0.1 }, ct);
        Assert.Equal(0.3, _lights.State.Brightness, 3);
        await _http.PostAsJsonAsync("brightness", new { delta = -5 }, ct);
        Assert.Equal(0, _lights.State.Brightness);
    }

    [Fact]
    public async Task Post_without_client_header_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var bare = new HttpClient();
        var res = await bare.PostAsync($"http://localhost:{Port}/api/power", null, ct);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.True(_lights.State.Enabled);
    }
}
