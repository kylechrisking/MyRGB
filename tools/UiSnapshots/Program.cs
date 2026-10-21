using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MyRGB.App;
using MyRGB.App.Services;
using MyRGB.App.ViewModels;
using MyRGB.Core;
using MyRGB.Core.Audio;
using MyRGB.Core.Devices;
using MyRGB.Cloud;
using MyRGB.Core.Presets;
using MyRGB.Core.Sensors;
using MyRGB.Core.Vendors;

// Usage: UiSnapshots <output folder>
// Renders the first-run tour and each page of the main window, with fake devices, batteries and
// vendor apps, into <output>/*.png. Never touches real hardware or the real settings (temp data
// folder, no API, no OpenRGB, no registry).
string outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "snapshots");
Directory.CreateDirectory(outDir);
foreach (var old in Directory.GetFiles(outDir, "*.png")) File.Delete(old);
string data = Path.Combine(Path.GetTempPath(), "MyRGB-snapshots");
if (Directory.Exists(data)) Directory.Delete(data, recursive: true);

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .WithInterFont()
    .SetupWithoutStarting();

InstalledProgram[] installed =
[
    new("Corsair iCUE5 Software", "Corsair", "5.52.93", "MsiExec.exe /I{A9B0B2D7-8C59-4413-A2FB-99EDBE65A608}"),
    new("Corsair Device Control Service", "Corsair", "1.9.18", "MsiExec.exe /X{38D4DBC1-C1E3-4664-8363-43BE072DD228}"),
    new("Corsair iCUE 5 VHID Drivers v3.0.88.9", "Corsair Memory", "3.0.88.9", "MsiExec.exe /X{FA0C115C-2532-4C79-8A2E-41A23E471B89}"),
    new("Logitech G HUB", "Logitech", "2026.4.919028", "\"C:\\Program Files\\LGHUB\\lghub_updater.exe\" --uninstall"),
    new("Elgato Stream Deck", "Corsair Memory, Inc.", "7.6.0", "MsiExec.exe /I{187BBE1D-5634-411D-A397-EEC5E7A87487}"),
];

var host = new AppHost(new AppHostOptions
{
    DataDirectory = data,
    EnableApi = false,
    Providers = (_, _) => [new FakeProvider()],
    BatteryProviders = () => [new FakeBatteries()],
    DetectVendorApps = () => VendorAppCatalog.Detect(installed, ["iCUE"]),
    Cloud = () => new FakeCloud(),
    DynamicLightingOn = () => true,
    ListenToAudio = false,
    Sensors = () => new FakeSensors(),
});
// Stay on this thread: it's Avalonia's UI thread, and awaiting would resume elsewhere.
host.StartAsync().GetAwaiter().GetResult();
host.Batteries.RefreshAsync().GetAwaiter().GetResult();
Thread.Sleep(500);

var vm = new MainViewModel(host);
var window = new MainWindow { DataContext = vm, Width = 1280, Height = 860 };
window.Show();

void Shoot(string name)
{
    for (int f = 0; f < 25; f++)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(40);
    }
    var frame = window.CaptureRenderedFrame();
    using (var file = File.Create(Path.Combine(outDir, $"{name}.png"))) frame?.Save(file);
    Console.WriteLine($"{name}: {(frame is null ? "no frame" : "ok")}");
}

Shoot("00-tour");                                    // first launch shows the tour
vm.Welcome!.NextCommand.Execute(null);
vm.Welcome!.NextCommand.Execute(null);
Shoot("01-tour-step2");
vm.Welcome!.SkipCommand.Execute(null);

vm.SelectedTab = MainViewModel.HomeTab; Shoot("02-home");
vm.SelectedTab = MainViewModel.PresetsTab; Shoot("03-presets");
vm.SelectedTab = MainViewModel.CommunityTab; Thread.Sleep(300); Shoot("03b-community");
vm.EditPreset("ocean"); Shoot("04-studio");
vm.Studio.SelectedTarget = vm.Studio.Targets[1]; Shoot("04b-studio-device");
vm.Studio.SelectedTarget = vm.Studio.Targets[0];
// A made-up spectrum (strong bass tapering to the treble) so the Visualizer has something to draw.
var song = Enumerable.Range(0, AudioFrame.BandCount).Select(b => (float)Math.Clamp(0.95 - b * 0.022 + 0.12 * Math.Sin(b * 1.3), 0, 1)).ToArray();
AudioFeed.Publish(new AudioFrame(0.8f, song, 3, song));
vm.EditPreset("visualizer"); Shoot("04c-visualizer");
AudioFeed.Publish(AudioFrame.Silent);
vm.SelectedTab = MainViewModel.LayoutTab; Shoot("05-layout");
vm.SelectedTab = MainViewModel.DevicesTab; Shoot("06-devices");
vm.Supported.Query = "razer basilisk"; Console.WriteLine($"supported search: {vm.Supported.Summary} first={vm.Supported.Results.FirstOrDefault()?.Name}");
for (int i = 0; i < 180; i++) host.Sensors.Poll(); // a few minutes of history for the charts
host.SetFan(new FanSetting { FanId = "fake/gpu/fan1", Mode = FanMode.Curve, SensorKey = SensorKeys.GpuTemp });
vm.SelectedTab = MainViewModel.MonitoringTab; Shoot("06b-monitoring");
vm.SelectedTab = MainViewModel.CoolingTab; Shoot("06c-cooling");
vm.SelectedTab = MainViewModel.VendorsTab; Shoot("07-vendors");
vm.Vendors.Apps[0].AskUninstallCommand.Execute(null); Shoot("08-vendors-confirm");
vm.SelectedTab = MainViewModel.SettingsTab; Shoot("09-settings");

window.AllowClose = true;
window.Close();
host.Shutdown();

/// <summary>Stand-ins shaped like a typical desk: keyboard, mouse, headset, RAM, fan hub, board.</summary>
sealed class FakeProvider : IDeviceProvider
{
    public string Id => "fake";
    public string DisplayName => "Demo devices";
    public int Priority => 0;
    public ProviderStatus Status => ProviderStatus.Connected;
    public string? StatusMessage => "11 devices";
    public event EventHandler? DevicesChanged { add { } remove { } }

    public Task<IReadOnlyList<IRgbDevice>> DiscoverAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IRgbDevice>>(
    [
        new FakeDevice("Corsair Vanguard Pro 96", "Corsair", DeviceType.Keyboard, Grid(20, 6), [new("Keys", 0, 120)], provider: "corsair"),
        new FakeDevice("Razer Basilisk V3 Pro (Wireless)", "Razer", DeviceType.Mouse,
            [new("Scroll", 0.5f, 0.15f), new("Logo", 0.5f, 0.7f), .. Enumerable.Range(0, 11).Select(i => new LedInfo($"Strip {i}", i < 6 ? 0f : 1f, 0.2f + (i % 6) * 0.15f))],
            [new("Scroll Wheel", 0, 1), new("Logo", 1, 1), new("LED Strip", 2, 11)], hardwareKey: "usb:1532:00ab"),
        new FakeDevice("G733 Gaming Headset", "Logitech", DeviceType.Headset, [new("Left", 0.2f, 0.5f), new("Right", 0.8f, 0.5f)], [new("Earcups", 0, 2)], hardwareKey: "usb:046d:0ab5"),
        new FakeDevice("Razer Base Station V2 Chroma", "Razer", DeviceType.HeadsetStand,
            Enumerable.Range(0, 8).Select(i => new LedInfo($"LED {i}", 0.5f + 0.45f * MathF.Cos(i * MathF.Tau / 8), 0.5f + 0.45f * MathF.Sin(i * MathF.Tau / 8))).ToArray(),
            [new("LED Strip", 0, 8)]),
        .. Enumerable.Range(0, 4).Select(i => new FakeDevice("Corsair Vengeance RGB Pro DDR4", "Corsair", DeviceType.Memory,
            // As OpenRGB reports them: one linear zone (a row), one stick per DIMM slot.
            Enumerable.Range(0, 10).Select(k => new LedInfo($"LED {k}", k / 9f, 0.5f)).ToArray(), [new("Stick", 0, 10)], id: $"ram{i}",
            slot: MemorySlots.FromLocation($"I2C: PawnIO SMBus i801 0, address 0x{0x58 + i:X2}"))),
        new FakeDevice("Corsair Lighting Node Core", "Corsair", DeviceType.LedStrip,
            Enumerable.Range(0, 48).Select(i => new LedInfo($"LED {i}", i / 47f, 0.5f)).ToArray(), [new("Fan Channel 1", 0, 48)])
        {
            ResizableZones = [new("Fan Channel 1", 48, 0, 204)],
        },
        // Six single-LED zones, which OpenRGB's layout stacks into a column.
        new FakeDevice("B560 DS3H AC-Y1", "Gigabyte", DeviceType.Motherboard,
            Enumerable.Range(0, 6).Select(i => new LedInfo($"LED {i}", 0.5f, i / 5f)).ToArray(),
            Enumerable.Range(0, 6).Select(i => new ZoneInfo($"Zone {i + 1}", i, 1)).ToArray()),
    ]);

    private static LedInfo[] Grid(int w, int h) =>
        Enumerable.Range(0, w * h).Select(i => new LedInfo($"Key {i}", (i % w) / (float)(w - 1), (i / w) / (float)(h - 1))).ToArray();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class FakeDevice(
    string name, string vendor, DeviceType type, IReadOnlyList<LedInfo> leds, IReadOnlyList<ZoneInfo> zones,
    string provider = "openrgb", string? hardwareKey = null, string? id = null, string? slot = null) : IResizableDevice
{
    public string? Slot => slot;
    public IReadOnlyList<ResizableZone> ResizableZones { get; init; } = [];
    public string Id => $"fake:{id ?? name}";
    public string Name => name;
    public string Vendor => vendor;
    public DeviceType Type => type;
    public string ProviderId => provider;
    public string? HardwareKey => hardwareKey;
    public IReadOnlyList<LedInfo> Leds => leds;
    public IReadOnlyList<ZoneInfo> Zones => zones;
    public ValueTask ApplyAsync(ReadOnlyMemory<Rgb> colors, CancellationToken ct = default) => ValueTask.CompletedTask;
}

sealed class FakeBatteries : IBatteryProvider
{
    public IReadOnlyList<IBatterySource> Discover() =>
    [
        new FakeBattery("usb:1532:00ab", "Razer Basilisk V3 Pro", new BatteryStatus(13, false)),
        new FakeBattery("usb:046d:0ab5", "Logitech G733", new BatteryStatus(44, false, Estimated: true)),
    ];

    private sealed class FakeBattery(string key, string name, BatteryStatus status) : IBatterySource
    {
        public string HardwareKey => key;
        public string Name => name;
        public Task<BatteryStatus?> ReadAsync(CancellationToken ct) => Task.FromResult<BatteryStatus?>(status);
    }
}

/// <summary>A signed-in account with a small community gallery, all in memory.</summary>
sealed class FakeCloud : ICloud
{
    private static Preset P(string name, string effect, params (string, string)[] s) =>
        new() { Id = name, Name = name, EffectId = effect, Settings = s.ToDictionary(x => x.Item1, x => x.Item2) };

    private readonly List<SharedPreset> _shared =
    [
        new(Guid.NewGuid(), "Synthwave Sunset", "Pink to purple, slow drift. Great with a dark desk.", "gradient",
            P("Synthwave Sunset", "gradient", ("from", "#FF2E97"), ("to", "#6A00FF")), ["#FF2E97", "#D11FB8", "#A010D8", "#7A04F0", "#6A00FF"], 1284, DateTimeOffset.Now.AddDays(-20), Guid.NewGuid(), "neonfox"),
        new(Guid.NewGuid(), "Deep Sea", "", "plasma", P("Deep Sea", "plasma", ("palette", "Ocean")), ["#001A50", "#0060D0", "#00C8E0", "#0090C0", "#003070"], 865, DateTimeOffset.Now.AddDays(-9), Guid.NewGuid(), "marlin", true),
        new(Guid.NewGuid(), "Toxic", "Green waves on black.", "color-wave", P("Toxic", "color-wave", ("color", "#7CFF00")), ["#000500", "#3A8000", "#7CFF00", "#3A8000", "#000500"], 402, DateTimeOffset.Now.AddDays(-3), Guid.NewGuid(), "kilowatt"),
        new(Guid.NewGuid(), "Ember", "Warm fire for late nights.", "fire", P("Ember", "fire"), ["#300000", "#A01000", "#FF5000", "#FF9020", "#FFC040"], 233, DateTimeOffset.Now.AddDays(-1), Guid.NewGuid(), "dusk"),
    ];

    public bool IsConfigured => true;
    public CloudUser? User { get; } = new(Guid.NewGuid(), "Alex Rivera", "alex@example.com", "google");
    public event EventHandler? UserChanged { add { } remove { } }
    public Task SignInAsync(string provider, CancellationToken ct) => Task.CompletedTask;
    public Task SignOutAsync() => Task.CompletedTask;
    public Task DeleteAccountAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<RemotePreset>> GetSyncedPresetsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RemotePreset>>([]);
    public Task PutSyncedPresetsAsync(IReadOnlyCollection<RemotePreset> presets, CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyList<SharedPreset>> BrowseAsync(string? search, CommunitySort sort, CancellationToken ct) => Task.FromResult<IReadOnlyList<SharedPreset>>(_shared);
    public Task ShareAsync(Preset preset, IReadOnlyList<string> swatch, string description, CancellationToken ct) => Task.CompletedTask;
    public Task DeleteSharedAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
    public Task CountInstallAsync(Guid id, CancellationToken ct) => Task.CompletedTask;
    public Task ReportAsync(Guid id, string reason, CancellationToken ct) => Task.CompletedTask;
    public Task SendSupportMessageAsync(string subject, string body, string appVersion, CancellationToken ct) => Task.CompletedTask;
    public Task<SupporterStatus?> GetSupportAsync(CancellationToken ct) => Task.FromResult<SupporterStatus?>(new(true, DateTimeOffset.Now.AddDays(300), true));
    public Task SetBadgeVisibleAsync(bool visible, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>A PC like the one MyRGB is developed on: an 8-core CPU, an RTX card with two fans.</summary>
sealed class FakeSensors : ISensorSource
{
    private int _t;
    private readonly FakeFan[] _fans = [new("fake/gpu/fan1", "GPU fan 1"), new("fake/gpu/fan2", "GPU fan 2")];

    public IReadOnlyList<IFanChannel> Fans => _fans;

    public IReadOnlyList<SensorReading> Read()
    {
        double t = _t++;
        double wave = Math.Sin(t / 17) * 0.5 + 0.5, spike = Math.Max(0, Math.Sin(t / 7)) * 0.4;
        double gpuTemp = 44 + 20 * wave, cpuLoad = 12 + 40 * wave * wave + 20 * spike;
        foreach (var f in _fans) f.Tick(gpuTemp);
        var r = new List<SensorReading>
        {
            new("fake/cpu/load", SensorCategory.Cpu, "11th Gen Intel Core i7-11700K", "CPU Total", SensorKind.Load, cpuLoad, SensorKeys.CpuLoad),
            new("fake/cpu/clock", SensorCategory.Cpu, "11th Gen Intel Core i7-11700K", "Clock", SensorKind.Clock, 4600 + 300 * wave, SensorKeys.CpuClock),
            new("fake/gpu/temp", SensorCategory.Gpu, "NVIDIA GeForce RTX 4070", "GPU Core", SensorKind.Temperature, gpuTemp, SensorKeys.GpuTemp),
            new("fake/gpu/hot", SensorCategory.Gpu, "NVIDIA GeForce RTX 4070", "GPU Hot Spot", SensorKind.Temperature, gpuTemp + 7, SensorKeys.GpuHotspot),
            new("fake/gpu/mem", SensorCategory.Gpu, "NVIDIA GeForce RTX 4070", "GPU Memory Junction", SensorKind.Temperature, gpuTemp + 4, SensorKeys.GpuMemoryTemp),
            new("fake/gpu/load", SensorCategory.Gpu, "NVIDIA GeForce RTX 4070", "GPU Core", SensorKind.Load, 70 * wave, SensorKeys.GpuLoad),
            new("fake/gpu/power", SensorCategory.Gpu, "NVIDIA GeForce RTX 4070", "GPU Package", SensorKind.Power, 30 + 150 * wave, SensorKeys.GpuPower),
            new("fake/gpu/vram", SensorCategory.Gpu, "NVIDIA GeForce RTX 4070", "GPU Memory Used", SensorKind.Data, 3.2, SensorKeys.GpuMemoryUsed),
            new("fake/gpu/vramt", SensorCategory.Gpu, "NVIDIA GeForce RTX 4070", "GPU Memory Total", SensorKind.Data, 12, SensorKeys.GpuMemoryTotal),
            new("fake/mem/load", SensorCategory.Memory, "Total Memory", "Memory", SensorKind.Load, 38 + 4 * wave, SensorKeys.MemoryLoad),
            new("fake/mem/used", SensorCategory.Memory, "Total Memory", "Memory Used", SensorKind.Data, 12.1 + wave, SensorKeys.MemoryUsed),
            new("fake/mem/total", SensorCategory.Memory, "Total Memory", "Memory Total", SensorKind.Data, 31.9, SensorKeys.MemoryTotal),
            new("fake/net/down", SensorCategory.Network, "Network", "Download", SensorKind.Throughput, 2.4e6 * spike + 4e4, SensorKeys.NetDown),
            new("fake/net/up", SensorCategory.Network, "Network", "Upload", SensorKind.Throughput, 1.8e5 * wave, SensorKeys.NetUp),
            new("fake/drive/c", SensorCategory.Storage, "C: Windows", "Used", SensorKind.Load, 62),
            new("fake/drive/c/u", SensorCategory.Storage, "C: Windows", "Used space", SensorKind.Data, 620),
            new("fake/drive/c/t", SensorCategory.Storage, "C: Windows", "Size", SensorKind.Data, 1000),
            new("fake/drive/e", SensorCategory.Storage, "E: Projects", "Used", SensorKind.Load, 41),
            new("fake/drive/e/u", SensorCategory.Storage, "E: Projects", "Used space", SensorKind.Data, 820),
            new("fake/drive/e/t", SensorCategory.Storage, "E: Projects", "Size", SensorKind.Data, 2000),
        };
        for (int i = 0; i < 16; i++)
            r.Add(new($"fake/cpu/thread{i}", SensorCategory.Cpu, "11th Gen Intel Core i7-11700K", $"CPU Core #{i / 2 + 1} Thread #{i % 2 + 1}",
                SensorKind.Load, Math.Clamp(cpuLoad + 30 * Math.Sin(i * 1.7 + t / 5), 0, 100)));
        return r;
    }

    public void Dispose() { }

    private sealed class FakeFan(string id, string name) : IFanChannel
    {
        private double? _set;
        public string Id => id;
        public string Name => name;
        public string GuardKey => SensorKeys.GpuTemp;
        public double? Rpm { get; private set; }
        public double? Percent { get; private set; }
        public void Tick(double temp)
        {
            Percent = _set ?? (temp < 50 ? 0 : 30 + (temp - 50) * 2);
            Rpm = Percent * 30;
        }
        public void Set(double percent) => _set = percent;
        public void Reset() => _set = null;
    }
}
