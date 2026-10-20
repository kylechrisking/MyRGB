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

    [Theory]
    [InlineData("I2C: PawnIO SMBus i801 0, address 0x58", "A1")]
    [InlineData("I2C: PawnIO SMBus i801 0, address 0x59", "A2")]
    [InlineData("I2C: PawnIO SMBus i801 0, address 0x5A", "B1")]
    [InlineData("I2C: PawnIO SMBus i801 0, address 0x5b", "B2")]
    [InlineData("I2C: SMBus PIIX4, address 0x5F", "D2")]
    [InlineData("I2C: SMBus PIIX4, address 0x70", null)]
    [InlineData("HID: \\\\?\\HID#VID_1B1C&PID_0C1A", null)]
    [InlineData(null, null)]
    public void Ram_slot_from_smbus_address(string? location, string? slot) =>
        Assert.Equal(slot, MemorySlots.FromLocation(location));

    [Fact]
    public void Ram_is_named_by_slot_and_falls_back_to_stick_numbers()
    {
        var names = DeviceNames.Unique([new RamStick("a", "B1"), new RamStick("b", "A1"), new RamStick("c", null), new RamStick("d", null)]);
        Assert.Equal("Vengeance (DIMM B1)", names["a"]);
        Assert.Equal("Vengeance (DIMM A1)", names["b"]);
        Assert.Equal("Vengeance (stick 3)", names["c"]);
        Assert.Equal("Vengeance (stick 4)", names["d"]);
    }

    [Fact]
    public void A_row_of_leds_stands_up_in_a_tall_box()
    {
        LedInfo[] row = [new("0", 0, 0.5f), new("1", 0.5f, 0.5f), new("2", 1, 0.5f)];
        var tall = new DevicePlacement("ram", 0.5, 0.1, 0.03, 0.2);
        var wide = new DevicePlacement("strip", 0.1, 0.1, 0.3, 0.05);

        Assert.True(tall.Turns(row));
        Assert.False(wide.Turns(row));
        // Turned, the row runs top to bottom down the middle of the stick.
        Assert.Equal((0.5 + 0.015, 0.1), tall.Map(row[0], turned: true));
        Assert.Equal((0.5 + 0.015, 0.1 + 0.2), tall.Map(row[2], turned: true));

        LedInfo[] column = [new("0", 0.5f, 0), new("1", 0.5f, 1)];
        Assert.True(wide.Turns(column));
        // A board's default box is taller than wide in canvas units but wider on screen (16:9).
        Assert.True(new DevicePlacement("board", 0.38, 0.05, 0.17, 0.27).Turns(column));
        Assert.False(wide.Turns([new("only", 0.5f, 0.5f)]));
    }

    private sealed class RamStick(string id, string? slot) : IRgbDevice
    {
        public string Id => id;
        public string Name => "Vengeance";
        public string Vendor => "Corsair";
        public DeviceType Type => DeviceType.Memory;
        public string ProviderId => "fake";
        public string? HardwareKey => null;
        public string? Slot => slot;
        public IReadOnlyList<LedInfo> Leds => [];
        public IReadOnlyList<ZoneInfo> Zones => [];
        public ValueTask ApplyAsync(ReadOnlyMemory<Rgb> colors, CancellationToken ct = default) => ValueTask.CompletedTask;
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
