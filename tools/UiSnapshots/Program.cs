using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using MyRGB.App;
using MyRGB.App.Services;
using MyRGB.App.ViewModels;
using MyRGB.Core;
using MyRGB.Core.Devices;
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
vm.EditPreset("ocean"); Shoot("04-studio");
vm.SelectedTab = MainViewModel.LayoutTab; Shoot("05-layout");
vm.SelectedTab = MainViewModel.DevicesTab; Shoot("06-devices");
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
            Enumerable.Range(0, 10).Select(k => new LedInfo($"LED {k}", 0.5f, k / 9f)).ToArray(), [new("Stick", 0, 10)], id: $"ram{i}")),
        new FakeDevice("Corsair Lighting Node Core", "Corsair", DeviceType.LedStrip,
            Enumerable.Range(0, 96).Select(i => new LedInfo($"LED {i}", (i % 16) / 15f, (i / 16) / 5f)).ToArray(), [new("Fan Channel 1", 0, 96)])
        {
            ResizableZones = [new("Fan Channel 1", 96, 0, 204)],
        },
        new FakeDevice("B560 DS3H AC-Y1", "Gigabyte", DeviceType.Motherboard,
            Enumerable.Range(0, 6).Select(i => new LedInfo($"LED {i}", i / 5f, 0.5f)).ToArray(), [new("Headers", 0, 6)]),
    ]);

    private static LedInfo[] Grid(int w, int h) =>
        Enumerable.Range(0, w * h).Select(i => new LedInfo($"Key {i}", (i % w) / (float)(w - 1), (i / w) / (float)(h - 1))).ToArray();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class FakeDevice(
    string name, string vendor, DeviceType type, IReadOnlyList<LedInfo> leds, IReadOnlyList<ZoneInfo> zones,
    string provider = "openrgb", string? hardwareKey = null, string? id = null) : IResizableDevice
{
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
