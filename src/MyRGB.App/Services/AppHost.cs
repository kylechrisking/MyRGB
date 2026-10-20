using Microsoft.Extensions.Logging;
using MyRGB.Api;
using MyRGB.Battery;
using MyRGB.Cloud;
using MyRGB.Core;
using MyRGB.Core.Devices;
using MyRGB.Core.Effects;
using MyRGB.Core.Engine;
using MyRGB.Core.Layout;
using MyRGB.Core.Presets;
using MyRGB.Core.Sensors;
using MyRGB.Core.Vendors;
using MyRGB.Providers.Corsair;
using MyRGB.Providers.OpenRgb;
using MyRGB.Sensors;

namespace MyRGB.App.Services;

public sealed class AppHostOptions
{
    /// <summary>Where settings and logs live. Defaults to %APPDATA%\MyRGB.</summary>
    public string DataDirectory { get; init; } = SettingsStore.DefaultDataDirectory;

    /// <summary>Replaces the real device providers (used by UI snapshots and tests).</summary>
    public Func<AppSettings, ILoggerFactory, IEnumerable<IDeviceProvider>>? Providers { get; init; }

    /// <summary>Replaces the real battery readers.</summary>
    public Func<IEnumerable<IBatteryProvider>>? BatteryProviders { get; init; }

    /// <summary>Replaces reading Installed apps and running processes.</summary>
    public Func<IReadOnlyList<DetectedVendorApp>>? DetectVendorApps { get; init; }

    /// <summary>Replaces the hardware sensors (CPU, GPU, fans).</summary>
    public Func<ISensorSource>? Sensors { get; init; }

    /// <summary>Replaces reading whether Windows Dynamic Lighting is on.</summary>
    public Func<bool?>? DynamicLightingOn { get; init; }

    /// <summary>Replaces the account service.</summary>
    public Func<ICloud>? Cloud { get; init; }

    public bool EnableApi { get; init; } = true;
}

/// <summary>Wires the engine, providers, presets, persistence and the local API together for the app's lifetime.</summary>
public sealed class AppHost : IAsyncDisposable
{
    /// <summary>Id the Studio uses for its unsaved, live-edited preset.</summary>
    public const string DraftPresetId = "draft";

    private readonly AppHostOptions _options;
    private readonly SettingsStore _store;
    private readonly ILogger _log;
    private CancellationTokenSource? _saveDebounce;
    private CancellationTokenSource? _syncDebounce;

    public AppHost(AppHostOptions? options = null)
    {
        _options = options ?? new AppHostOptions();
        _store = new SettingsStore(_options.DataDirectory);
        Settings = _store.Load();
        LogFile = new FileLoggerProvider(Path.Combine(_options.DataDirectory, "logs", "myrgb.log"));
        Logs = LoggerFactory.Create(b => b.AddProvider(LogFile).SetMinimumLevel(LogLevel.Information));
        _log = Logs.CreateLogger<AppHost>();

        Devices = new DeviceManager(Logs.CreateLogger<DeviceManager>());
        var providers = _options.Providers?.Invoke(Settings, Logs) ??
        [
            new CorsairProvider(Logs.CreateLogger<CorsairProvider>()),
            new OpenRgbProvider(Settings.OpenRgb, Logs.CreateLogger<OpenRgbProvider>(), () => Settings.ZoneSizes),
            // More native providers (Razer, Logitech, ...) register here as they're written.
        ];
        foreach (var p in providers) Devices.AddProvider(p);

        Effects = EffectRegistry.CreateDefault();
        Presets = new PresetLibrary(Settings.UserPresets);
        Layout = new CanvasLayout(Settings.Layout);
        Engine = new LightingEngine(Devices, Effects, Layout, Logs.CreateLogger<LightingEngine>())
        {
            Fps = Settings.Fps,
            Brightness = Settings.Brightness,
            Enabled = Settings.Enabled,
        };
        Engine.ExcludedDevices = Settings.ExcludedDevices.ToHashSet();
        Lights = new LightingService(Engine, Presets, Effects, Devices);
        Batteries = new BatteryMonitor(
            _options.BatteryProviders?.Invoke() ?? [new RazerBatteryProvider(), new LogitechBatteryProvider()],
            Logs.CreateLogger<BatteryMonitor>());

        Cloud = _options.Cloud?.Invoke()
                ?? (CloudConfig.Load() is { } cloud ? new SupabaseCloud(cloud, _options.DataDirectory) : new OfflineCloud());
        Sync = new PresetSync(Cloud, Presets, _options.DataDirectory);

        Sensors = new SensorHub(_options.Sensors?.Invoke() ?? OpenSensors(), Logs.CreateLogger<SensorHub>());
        Sensors.SetFanSettings(Settings.Fans);
        SensorFeed.Use(Sensors.Get);

        Devices.DevicesChanged += (_, _) => WatchDeviceCommands();
        Lights.StateChanged += (_, _) => ScheduleSave();
        Presets.Changed += (_, _) => { ScheduleSave(); ScheduleSync(); };
        Layout.Changed += (_, _) => ScheduleSave();
        Cloud.UserChanged += (_, _) => { if (Cloud.User is not null) ScheduleSync(TimeSpan.Zero); };
    }

    public ICloud Cloud { get; }
    public PresetSync Sync { get; }

    /// <summary>Sync presets a few seconds after the last change, so a burst of edits is one upload.</summary>
    public void ScheduleSync(TimeSpan? delay = null)
    {
        if (Cloud.User is null || !Settings.SyncPresets) return;
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _syncDebounce, cts)?.Cancel();
        _ = Task.Delay(delay ?? TimeSpan.FromSeconds(5), cts.Token).ContinueWith(async t =>
        {
            if (t.IsCanceled) return;
            if (await Sync.SyncAsync() is { } error) _log.LogWarning("Preset sync: {Error}", error);
        }, TaskScheduler.Default);
    }

    public AppSettings Settings { get; }
    public FileLoggerProvider LogFile { get; }
    public ILoggerFactory Logs { get; }
    public DeviceManager Devices { get; }
    public EffectRegistry Effects { get; }
    public PresetLibrary Presets { get; }
    public CanvasLayout Layout { get; }
    public LightingEngine Engine { get; }
    public LightingService Lights { get; }
    public BatteryMonitor Batteries { get; }
    public SensorHub Sensors { get; }

    private readonly HashSet<IDeviceCommands> _commandSources = [];

    /// <summary>Listen to buttons on newly found devices (a keyboard's brightness keys, say).</summary>
    private void WatchDeviceCommands()
    {
        lock (_commandSources)
            foreach (var d in Devices.Devices.OfType<IDeviceCommands>())
                if (_commandSources.Add(d)) d.Command += OnDeviceCommand;
    }

    private void OnDeviceCommand(IRgbDevice device, DeviceCommand command)
    {
        double b = Engine.Brightness;
        switch (command)
        {
            case DeviceCommand.BrightnessUp: Lights.SetBrightness(Math.Min(1, b + 0.1)); break;
            case DeviceCommand.BrightnessDown: Lights.SetBrightness(Math.Max(0, b - 0.1)); break;
            case DeviceCommand.BrightnessNudgeUp: Lights.SetBrightness(Math.Min(1, b + 0.05)); break;
            case DeviceCommand.BrightnessNudgeDown: Lights.SetBrightness(Math.Max(0, b - 0.05)); break;
            case DeviceCommand.NextPreset: Lights.StepPreset(1); break;
            // A short flash on the device itself says which way the dial now goes.
            case DeviceCommand.DialControlsBrightness: Lights.Flash([device.Id], new Rgb(255, 170, 30), TimeSpan.FromSeconds(0.8)); break;
            case DeviceCommand.DialControlsVolume: Lights.Flash([device.Id], new Rgb(40, 140, 255), TimeSpan.FromSeconds(0.8)); break;
        }
    }

    /// <summary>Change how one fan is driven; takes effect within a second and is saved.</summary>
    public void SetFan(FanSetting setting)
    {
        Settings.Fans = [.. Settings.Fans.Where(f => !f.FanId.Equals(setting.FanId, StringComparison.OrdinalIgnoreCase)), setting];
        Sensors.SetFanSettings(Settings.Fans);
        ScheduleSave();
    }

    private ISensorSource OpenSensors()
    {
        try { return new HardwareSensors(); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Hardware sensors unavailable");
            return new NoSensors();
        }
    }
    public LocalApiServer? Api { get; private set; }

    /// <summary>Whether Windows Dynamic Lighting is switched on (null if this Windows has none).</summary>
    public bool? DynamicLightingOn() => _options.DynamicLightingOn is { } fake ? fake() : DynamicLighting.IsOn();

    /// <summary>Vendor lighting apps installed on this PC (iCUE, G HUB, ...). Reads the registry; call off the UI thread.</summary>
    public IReadOnlyList<DetectedVendorApp> DetectVendorApps()
    {
        if (_options.DetectVendorApps is { } fake) return fake();
        try
        {
            return VendorAppCatalog.Detect(InstalledPrograms.Read(), InstalledPrograms.RunningProcessNames());
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Looking for vendor apps failed");
            return [];
        }
    }
    public string? ApiError { get; private set; }

    /// <summary>Raised (any thread) when something outside the window, e.g. a second launch, asks to show it.</summary>
    public event Action? ShowRequested;

    public async Task StartAsync()
    {
        // Through Lights, not the engine, so the window (built before this runs) hears which preset is on.
        var preset = (Settings.ActivePresetId is { } id ? Presets.Find(id) : null) ?? Presets.BuiltIns[0];
        Lights.ActivatePreset(preset.Id);
        Engine.Start();
        WatchDeviceCommands();
        Batteries.Start();
        Sensors.Start();

        ScheduleSync(TimeSpan.FromSeconds(3)); // pick up edits made on other PCs since last time

        if (_options.EnableApi) await StartApiAsync();
        else ApiError = "disabled";

        _ = Task.Run(async () =>
        {
            try { await Devices.RefreshAsync(); }
            catch (Exception ex) { _log.LogError(ex, "Initial device scan failed"); }
        });
    }

    private async Task StartApiAsync()
    {
        try
        {
            Api = new LocalApiServer(Lights, Settings.ApiPort, Logs.CreateLogger<LocalApiServer>(), () => ShowRequested?.Invoke());
            await Api.StartAsync();
            _log.LogInformation("Local API on {Url}", Api.BaseUrl);
        }
        catch (Exception ex)
        {
            ApiError = ex.Message;
            Api = null;
            _log.LogError(ex, "Local API failed to start on port {Port}", Settings.ApiPort);
        }
    }

    public void SetExcluded(string deviceId, bool excluded)
    {
        var set = Settings.ExcludedDevices.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (excluded) set.Add(deviceId); else set.Remove(deviceId);
        Settings.ExcludedDevices = [.. set];
        Engine.ExcludedDevices = set;
        ScheduleSave();
    }

    /// <summary>Save a zone's LED count and rescan so the provider applies it.</summary>
    public async Task SetZoneSizeAsync(string deviceId, string zone, int ledCount)
    {
        // Replace the list rather than mutate it: discovery reads it from another thread.
        Settings.ZoneSizes =
        [
            .. Settings.ZoneSizes.Where(z => !(string.Equals(z.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) && z.Zone == zone)),
            new ZoneSize(deviceId, zone, ledCount),
        ];
        ScheduleSave();
        await Devices.RefreshAsync();
    }

    public void SetFps(int fps)
    {
        Engine.Fps = fps;
        Settings.Fps = Engine.Fps;
        ScheduleSave();
    }

    public void ScheduleSave()
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _saveDebounce, cts)?.Cancel();
        _ = Task.Delay(TimeSpan.FromSeconds(1), cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) SaveNow();
        }, TaskScheduler.Default);
    }

    public void SaveNow()
    {
        try
        {
            var active = Engine.ActivePreset;
            if (active is not null && active.Id != DraftPresetId) Settings.ActivePresetId = active.Id;
            Settings.Brightness = Engine.Brightness;
            Settings.Enabled = Engine.Enabled;
            Settings.UserPresets = [.. Presets.UserPresets];
            Settings.Layout = [.. Layout.Placements];
            _store.Save(Settings);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Saving settings failed");
        }
    }

    /// <summary>
    /// Dispose from a synchronous caller such as the UI thread at exit. Runs on the thread pool:
    /// awaiting on the UI thread while blocking it would deadlock. Gives up after a few seconds
    /// rather than hang the exit.
    /// </summary>
    public void Shutdown() => Task.Run(() => DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(5));

    public async ValueTask DisposeAsync()
    {
        _saveDebounce?.Cancel();
        SaveNow();
        Engine.Dispose();
        Sensors.Dispose(); // hands any fans MyRGB was driving back to the hardware
        await Batteries.DisposeAsync();
        _syncDebounce?.Cancel();
        (Cloud as IDisposable)?.Dispose();
        if (Api is not null) await Api.DisposeAsync();
        await Devices.DisposeAsync();
        Logs.Dispose();
    }
}
