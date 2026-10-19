using MyRGB.Core;
using MyRGB.Core.Devices;
using MyRGB.Core.Effects;
using MyRGB.Core.Engine;
using MyRGB.Core.Layout;
using MyRGB.Core.Presets;

namespace MyRGB.Tests;

public class CoreTests
{
    [Theory]
    [InlineData(0, 255, 0, 0)]
    [InlineData(120, 0, 255, 0)]
    [InlineData(240, 0, 0, 255)]
    [InlineData(360, 255, 0, 0)]
    [InlineData(-120, 0, 0, 255)]
    public void Hsv_primaries(double hue, byte r, byte g, byte b) =>
        Assert.Equal(new Rgb(r, g, b), Rgb.FromHsv(hue, 1, 1));

    [Theory]
    [InlineData("#FF8000", 255, 128, 0)]
    [InlineData("0af", 0, 170, 255)]
    public void Parses_hex(string hex, byte r, byte g, byte b) => Assert.Equal(new Rgb(r, g, b), Rgb.Parse(hex));

    [Fact]
    public void Settings_fall_back_to_parameter_defaults()
    {
        var def = EffectRegistry.CreateDefault().Find("color-wave")!;
        var s = def.Bind(new EffectSettings(new Dictionary<string, string> { ["speed"] = "7" }));
        Assert.Equal(7, s.GetNumber("speed"));
        Assert.Equal(30, s.GetNumber("WIDTH")); // default, case-insensitive
        Assert.Equal("Right", s.GetString("direction"));
    }

    [Fact]
    public void Every_builtin_preset_renders()
    {
        var effects = EffectRegistry.CreateDefault();
        foreach (var p in BuiltInPresets.All)
        {
            Assert.NotNull(effects.Find(p.EffectId));
            var swatch = PresetLibrary.Swatch(p, effects);
            Assert.Equal(5, swatch.Length);
        }
        Assert.All(PresetLibrary.Swatch(BuiltInPresets.All.Single(p => p.Id == "lights-off"), effects), c => Assert.Equal(Rgb.Black, c));
    }

    [Fact]
    public void Merge_prefers_higher_priority_provider_for_same_hardware()
    {
        var openrgb = new FakeProvider("openrgb", 0);
        var native = new FakeProvider("razer", 10);
        var a = new FakeDevice("openrgb:mouse", "usb:1532:00ab");
        var b = new FakeDevice("razer:mouse", "usb:1532:00ab");
        var c = new FakeDevice("openrgb:ram", null);

        var merged = DeviceManager.Merge([(openrgb, a), (openrgb, c), (native, b)]);
        Assert.Equal(["razer:mouse", "openrgb:ram"], merged.Select(d => d.Id));
    }

    [Fact]
    public void Engine_applies_brightness_power_and_overrides()
    {
        var effects = EffectRegistry.CreateDefault();
        var kb = new FakeDevice("kb", null, 4);
        var mouse = new FakeDevice("mouse", null, 2);
        var preset = new Preset
        {
            Id = "t", Name = "t", EffectId = "static",
            Settings = new() { ["color"] = "#C86400" },
            Overrides = [new DeviceOverride { DeviceId = "mouse", Off = true }],
        };
        var plan = LightingEngine.RenderPlan.Build([kb, mouse], preset, effects, new CanvasLayout(), null);
        var log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        plan.Render(new FrameContext(0, 0.03), enabled: true, brightness: 0.5, log);
        Assert.All(kb.Last!, c => Assert.Equal(new Rgb(100, 50, 0), c));
        Assert.All(mouse.Last!, c => Assert.Equal(Rgb.Black, c));

        plan.Render(new FrameContext(0.05, 0.03), enabled: false, brightness: 1, log);
        Assert.All(kb.Last!, c => Assert.Equal(Rgb.Black, c));
    }

    [Fact]
    public void Engine_skips_unchanged_frames_until_keepalive()
    {
        var kb = new FakeDevice("kb", null, 3);
        var preset = new Preset { Id = "t", Name = "t", EffectId = "static" };
        var plan = LightingEngine.RenderPlan.Build([kb], preset, EffectRegistry.CreateDefault(), new CanvasLayout(), null);
        var log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        plan.Render(new FrameContext(0, 0.03), true, 1, log);
        plan.Render(new FrameContext(0.03, 0.03), true, 1, log);
        plan.Render(new FrameContext(0.5, 0.03), true, 1, log);
        Assert.Equal(1, kb.Writes);
        plan.Render(new FrameContext(1.1, 0.03), true, 1, log);
        Assert.Equal(2, kb.Writes);
    }

    [Fact]
    public void Step_preset_wraps_around()
    {
        var effects = EffectRegistry.CreateDefault();
        var devices = new DeviceManager();
        using var engine = new LightingEngine(devices, effects, new CanvasLayout());
        var svc = new LightingService(engine, new PresetLibrary(), effects, devices);

        svc.ActivatePreset(BuiltInPresets.All[^1].Id);
        svc.StepPreset(1);
        Assert.Equal(BuiltInPresets.All[0].Id, svc.State.ActivePresetId);
        svc.StepPreset(-1);
        Assert.Equal(BuiltInPresets.All[^1].Id, svc.State.ActivePresetId);
    }

    private sealed class FakeProvider(string id, int priority) : IDeviceProvider
    {
        public string Id => id;
        public string DisplayName => id;
        public int Priority => priority;
        public ProviderStatus Status => ProviderStatus.Connected;
        public string? StatusMessage => null;
        public event EventHandler? DevicesChanged { add { } remove { } }
        public Task<IReadOnlyList<IRgbDevice>> DiscoverAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IRgbDevice>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeDevice(string id, string? hardwareKey, int leds = 1) : IRgbDevice
    {
        public string Id => id;
        public string Name => id;
        public string Vendor => "Test";
        public DeviceType Type => DeviceType.Keyboard;
        public string ProviderId => "fake";
        public string? HardwareKey => hardwareKey;
        public IReadOnlyList<LedInfo> Leds { get; } = Enumerable.Range(0, leds).Select(i => new LedInfo($"L{i}", i / (float)Math.Max(1, leds - 1), 0.5f)).ToArray();
        public IReadOnlyList<ZoneInfo> Zones => [];
        public Rgb[]? Last { get; private set; }
        public int Writes { get; private set; }

        public ValueTask ApplyAsync(ReadOnlyMemory<Rgb> colors, CancellationToken ct = default)
        {
            Last = colors.ToArray();
            Writes++;
            return ValueTask.CompletedTask;
        }
    }
}
