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
    public async Task Power_off_for_a_while()
    {
        var ct = TestContext.Current.CancellationToken;
        var state = await (await _http.PostAsJsonAsync("power", new { on = false, minutes = 30 }, ct)).Content.ReadFromJsonAsync<LightingState>(Json, ct);
        Assert.False(state!.Enabled);
        Assert.StartsWith("turned off for a while", state.HeldBy);
        Assert.InRange(_lights.OffUntil!.Value, DateTime.Now.AddMinutes(29), DateTime.Now.AddMinutes(31));
    }

    private sealed class FakeMonitors : IMonitorBrightness
    {
        public bool Available => true;
        public int Level { get; private set; } = 70;
        public IReadOnlyList<string> Monitors => ["DELL", "ASUS"];
        public void SetLevel(int level) => Level = level;
    }

    [Fact]
    public async Task Monitor_brightness()
    {
        var ct = TestContext.Current.CancellationToken;
        var none = await _http.GetFromJsonAsync<LocalApiServer.MonitorsState>("monitors", Json, ct);
        Assert.False(none!.Available);

        var fake = new FakeMonitors();
        _server.Monitors = fake;
        var state = await _http.GetFromJsonAsync<LocalApiServer.MonitorsState>("monitors", Json, ct);
        Assert.Equal(70, state!.Level);
        Assert.Equal(["DELL", "ASUS"], state.Monitors);
        await _http.PostAsJsonAsync("monitors", new { value = 40 }, ct);
        Assert.Equal(40, fake.Level);
        await _http.PostAsJsonAsync("monitors", new { delta = -50 }, ct);
        Assert.Equal(0, fake.Level);
    }

    [Fact]
    public async Task Rescan_devices()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await _http.PostAsync("devices/rescan", null, ct)).StatusCode);

        int scans = 0;
        _server.Rescan = () => { scans++; return Task.CompletedTask; };
        Assert.True((await _http.PostAsync("devices/rescan", null, ct)).IsSuccessStatusCode);
        Assert.Equal(1, scans);
    }

    [Fact]
    public async Task Wallpaper_reads_and_changes()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await _http.GetAsync("wallpaper", ct)).StatusCode);

        var state = new LocalApiServer.WallpaperState(false, "lights", "lava-lamp", null);
        LocalApiServer.WallpaperRequest? asked = null;
        _server.Wallpaper = () => state;
        _server.SetWallpaper = body => { asked = body; return state = state with { On = body?.On ?? !state.On, Source = body?.Source ?? state.Source }; };

        var read = await (await _http.GetAsync("wallpaper", ct)).Content.ReadFromJsonAsync<LocalApiServer.WallpaperState>(Json, ct);
        Assert.Equal("lava-lamp", read!.PresetId);
        var changed = await (await _http.PostAsJsonAsync("wallpaper", new { on = true, source = "preset" }, ct)).Content.ReadFromJsonAsync<LocalApiServer.WallpaperState>(Json, ct);
        Assert.True(changed!.On);
        Assert.Equal("preset", asked!.Source);
    }

    [Fact]
    public async Task Pause_shuffle_and_flash()
    {
        var ct = TestContext.Current.CancellationToken;
        _lights.ActivatePreset("fire");

        await _http.PostAsync("pause", null, ct); // empty body toggles
        Assert.True(_lights.State.Paused);
        var state = await (await _http.PostAsJsonAsync("pause", new { paused = false }, ct)).Content.ReadFromJsonAsync<LightingState>(Json, ct);
        Assert.False(state!.Paused);

        (await _http.PostAsync("presets/shuffle", null, ct)).EnsureSuccessStatusCode();
        Assert.NotEqual("fire", _lights.State.ActivePresetId);

        (await _http.PostAsync("flash", null, ct)).EnsureSuccessStatusCode();
        Assert.True(_engine.Flashing);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.PostAsync("devices/nope/flash", null, ct)).StatusCode);
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
