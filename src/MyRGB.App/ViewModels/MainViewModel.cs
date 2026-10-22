using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyRGB.App.Controls;
using MyRGB.App.Services;
using MyRGB.Core;
using MyRGB.Core.Devices;
using MyRGB.Core.Effects;
using MyRGB.Core.Presets;

namespace MyRGB.App.ViewModels;

public sealed partial class PresetCardViewModel(PresetSummary summary, MainViewModel owner) : ObservableObject
{
    public string Id => summary.Id;
    public string Name => summary.Name;
    public bool IsBuiltIn => summary.BuiltIn;
    public string Badge => summary.BuiltIn ? "Built-in" : "Custom";

    private readonly Preset? _preset = owner.Host.Presets.Find(summary.Id);

    /// <summary>The preset's main effect and settings, for its live preview tile.</summary>
    public EffectDefinition? Effect => _preset is null ? null : owner.Host.Effects.Find(_preset.EffectId);
    public IReadOnlyDictionary<string, string>? Settings => _preset?.Settings;
    public bool HasEffect => Effect is not null;

    private readonly Color[] _colors = summary.Swatch.Select(h => Rgb.TryParse(h, out var c) ? Color.FromRgb(c.R, c.G, c.B) : Colors.Black).ToArray();

    private IBrush? _swatch;
    public IBrush Swatch => _swatch ??= MakeBrush(_colors);

    /// <summary>The active card is outlined in its own colors and glows in its brightest one.</summary>
    public IBrush Outline => IsActive ? Swatch : Brushes.Transparent;

    public BoxShadows Glow => IsActive
        ? new BoxShadows(new BoxShadow { Blur = 26, Spread = 1, Color = Color.FromArgb(110, Brightest.R, Brightest.G, Brightest.B) })
        : default;

    private Color Brightest => _colors.Length == 0 ? Colors.White : _colors.MaxBy(c => c.R + c.G + c.B);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Outline), nameof(Glow))]
    private bool _isActive;

    [RelayCommand] private void Activate() => owner.ActivatePreset(Id);
    [RelayCommand] private void Edit() => owner.EditPreset(Id);
    [RelayCommand] private void Delete() => owner.DeletePreset(Id);

    private static IBrush MakeBrush(IReadOnlyList<Color> colors)
    {
        if (colors.Count == 0) return Brushes.Black;
        if (colors.Count == 1) return new SolidColorBrush(colors[0]);
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        };
        for (int i = 0; i < colors.Count; i++)
            brush.GradientStops.Add(new GradientStop(colors[i], i / (double)(colors.Count - 1)));
        return brush;
    }
}

/// <summary>Something on Home that wants attention, with an optional button that deals with it.</summary>
public sealed record AlertViewModel(bool Warning, string Text, string? ActionLabel = null, IRelayCommand? Action = null)
{
    public Geometry Icon => Warning ? NavIcons.Warning : NavIcons.Info;
    public IBrush Accent => new SolidColorBrush(Warning ? Color.FromRgb(252, 180, 66) : Color.FromRgb(74, 186, 244));
    public bool HasAction => ActionLabel is not null;
}

public sealed partial class MainViewModel : ObservableObject
{
    // Sidebar order. Keep in sync with the TabItems in MainWindow.axaml.
    public const int HomeTab = 0, PresetsTab = 1, CommunityTab = 2, StudioTab = 3, LayoutTab = 4, DevicesTab = 5,
        MonitoringTab = 6, CoolingTab = 7, VendorsTab = 8, SettingsTab = 9;

    private const int LowBattery = 20;

    private readonly AppHost _host;
    private bool _syncing;

    public MainViewModel(AppHost host)
    {
        _host = host;
        Studio = new StudioViewModel(host);
        Vendors = new VendorAppsViewModel(host);
        Account = new AccountViewModel(host);
        Community = new CommunityViewModel(host, Account);
        Monitoring = new MonitoringViewModel(host);
        Cooling = new CoolingViewModel(host);
        SupportNudge = new SupportNudgeViewModel(host, Account);
        Supported.Search();
        _fps = host.Engine.Fps;
        _startMinimized = host.Settings.StartMinimized;
        _showHomeGuide = !host.Settings.HideHomeGuide;
        _startWithWindows = StartupRegistration.IsEnabled;
        _openRgbPath = host.Settings.OpenRgb.ExecutablePath ?? "";

        host.Lights.StateChanged += (_, _) => Dispatcher.UIThread.Post(SyncState);
        host.Presets.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshPresets);
        host.Devices.DevicesChanged += (_, _) => Dispatcher.UIThread.Post(RefreshDevices);
        host.Batteries.Changed += (_, _) => Dispatcher.UIThread.Post(ApplyBatteries);
        Vendors.Updated += RefreshAlerts;
        host.Sensors.Updated += () => Dispatcher.UIThread.Post(RefreshSensors);

        RefreshPresets();
        RefreshDevices();
        SyncState();
        _ = Vendors.Check();

        if (!host.Settings.HasSeenWelcome) OpenTour();

        // First look a minute in, once the account has said whether this is a supporter; then hourly,
        // since MyRGB usually runs for days at a time.
        var nudge = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        nudge.Tick += (_, _) =>
        {
            nudge.Interval = TimeSpan.FromHours(1);
            SupportNudge.Check(DateTime.Now);
        };
        nudge.Start();
    }

    public AppHost Host => _host;
    public StudioViewModel Studio { get; }
    public VendorAppsViewModel Vendors { get; }
    public AccountViewModel Account { get; }
    public CommunityViewModel Community { get; }
    public MonitoringViewModel Monitoring { get; }
    public CoolingViewModel Cooling { get; }
    public SupportNudgeViewModel SupportNudge { get; }
    public SupportedDevicesViewModel Supported { get; } = new();

    /// <summary>Sensor pages only redraw while they're showing; the hub keeps reading either way.</summary>
    private void RefreshSensors()
    {
        // Home shows a summary of both.
        if (SelectedTab is MonitoringTab or HomeTab) Monitoring.Refresh();
        if (SelectedTab is CoolingTab or HomeTab) Cooling.Refresh();
    }

    /// <summary>Builds without an account service hide Community and the account panels.</summary>
    public bool CloudAvailable => _host.Cloud.IsConfigured;

    partial void OnSelectedTabChanged(int value)
    {
        if (value == CommunityTab && !Community.LoadedOnce) _ = Community.Load();
        if (value is MonitoringTab or CoolingTab) RefreshSensors();
        // Opening the Studio from the menu starts from whatever is playing, not an empty editor.
        if (value == StudioTab && !Studio.HasLoaded)
            Studio.Load(_host.Engine.ActivePreset is { } p && p.Id != AppHost.DraftPresetId ? p : _host.Presets.BuiltIns[0]);
    }

    public ObservableCollection<PresetCardViewModel> Presets { get; } = [];
    public ObservableCollection<PresetCardViewModel> QuickPresets { get; } = [];
    public ObservableCollection<DeviceRowViewModel> Devices { get; } = [];
    public ObservableCollection<BatteryOnlyViewModel> OtherBatteries { get; } = [];
    public ObservableCollection<AlertViewModel> Alerts { get; } = [];

    public string ApiUrl => _host.Api is { } api ? api.BaseUrl : $"Not running: {_host.ApiError}";
    public string LogPath => _host.LogFile.FilePath;

    public string StreamDeckStatus
    {
        get
        {
            var plugins = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Elgato", "StreamDeck", "Plugins");
            if (Directory.Exists(Path.Combine(plugins, "com.myrgb.streamdeck.sdPlugin")))
                return "Plugin installed. Find MyRGB in Stream Deck's action list (restart Stream Deck once if it's not there yet).";
            return Directory.Exists(plugins)
                ? "Stream Deck is installed but the MyRGB plugin isn't. Run MyRGB setup again and tick the Stream Deck plugin."
                : "No Stream Deck software found on this PC.";
        }
    }

    [ObservableProperty] private int _selectedTab;

    /// <summary>The card of the preset that's on, for Home's live preview; null for the Studio's unsaved draft.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveHasEffect))]
    private PresetCardViewModel? _activeCard;

    public bool ActiveHasEffect => ActiveCard?.HasEffect == true;

    [ObservableProperty] private bool _showHomeGuide;

    [RelayCommand]
    private void HideHomeGuide()
    {
        ShowHomeGuide = false;
        _host.Settings.HideHomeGuide = true;
        _host.ScheduleSave();
    }
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private double _brightness;
    [ObservableProperty] private string _activePresetName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseIcon), nameof(PauseTip), nameof(PlayingLabel))]
    private bool _paused;

    public Geometry PauseIcon => Paused ? NavIcons.Play : NavIcons.Pause;
    public string PauseTip => Paused ? "Resume the animation" : "Freeze the animation where it is";
    public string PlayingLabel => Paused ? "PAUSED" : "NOW PLAYING";
    [ObservableProperty] private IBrush? _activeSwatch;
    [ObservableProperty] private string _providerStatus = "Looking for your devices…";
    [ObservableProperty] private string _homeSummary = "Looking for your devices…";
    [ObservableProperty] private int _fps;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private string _openRgbPath;
    [ObservableProperty] private bool _isScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWelcome))]
    private WelcomeViewModel? _welcome;

    public bool ShowWelcome => Welcome is not null;
    public bool HasOtherBatteries => OtherBatteries.Count > 0;
    public bool HasAlerts => Alerts.Count > 0;
    public bool HasDevices => Devices.Count > 0;

    public void ActivatePreset(string id) => _host.Lights.ActivatePreset(id);

    public void EditPreset(string id)
    {
        if (_host.Presets.Find(id) is not { } preset) return;
        Studio.Load(preset);
        SelectedTab = StudioTab;
    }

    public void DeletePreset(string id) => _host.Presets.Delete(id);

    /// <summary>Jump to a page by name; used by Home's buttons and the tour.</summary>
    [RelayCommand]
    private void Navigate(string? page)
    {
        SelectedTab = page switch
        {
            "home" => HomeTab,
            "presets" => PresetsTab,
            "studio" => StudioTab,
            "layout" => LayoutTab,
            "devices" => DevicesTab,
            "monitoring" => MonitoringTab,
            "cooling" => CoolingTab,
            "vendors" => VendorsTab,
            "community" => CommunityTab,
            "settings" or "account" => SettingsTab,
            _ => SelectedTab,
        };
    }

    public const string WebsiteUrl = "https://www.rootlabs.us";

    [RelayCommand]
    private static void OpenWebsite() => AccountViewModel.OpenLink(WebsiteUrl);

    [RelayCommand]
    private void OpenTour() => Welcome = new WelcomeViewModel(page =>
    {
        Welcome = null;
        _host.Settings.HasSeenWelcome = true;
        _host.ScheduleSave();
        if (page is not null) Navigate(page);
    },
    flash: color =>
    {
        if (color is { } c) _host.Lights.Flash(null, c, TimeSpan.FromMinutes(2));
        else _host.Lights.StopFlash();
    },
    deviceCount: () => Devices.Count(d => !d.NeedsSetup));

    [RelayCommand]
    private void NewPreset()
    {
        Studio.Load(new Preset { Id = AppHost.DraftPresetId, Name = "My preset", EffectId = "rainbow-wave" });
        SelectedTab = StudioTab;
    }

    [RelayCommand] private void NextPreset() => _host.Lights.StepPreset(1);
    [RelayCommand] private void PreviousPreset() => _host.Lights.StepPreset(-1);

    [RelayCommand]
    private void OpenDynamicLighting()
    {
        try { DynamicLighting.OpenSettings(); }
        catch (Exception) { /* no Settings app (e.g. Windows 10); nothing more to do */ }
    }
    [RelayCommand] private void Shuffle() => _host.Lights.Shuffle();
    [RelayCommand] private void TogglePause() => _host.Lights.SetPaused(!_host.Lights.State.Paused);

    /// <summary>Open what's playing in the Studio; the Studio's own draft just switches to it.</summary>
    [RelayCommand]
    private void EditActive()
    {
        if (_host.Engine.ActivePreset is { } p && p.Id != AppHost.DraftPresetId) EditPreset(p.Id);
        else SelectedTab = StudioTab;
    }
    [RelayCommand] private void TogglePower() => _host.Lights.SetEnabled(!_host.Lights.State.Enabled);
    [RelayCommand] private void ResetLayout() => _host.Layout.Reset();
    [RelayCommand] private void TidyLayout() => _host.Layout.Tidy(_host.Devices.Devices.Select(d => d.Id));

    [RelayCommand]
    private async Task Rescan()
    {
        IsScanning = true;
        ProviderStatus = "Looking for your devices…";
        try
        {
            await Task.Run(async () =>
            {
                await _host.Devices.RefreshAsync();
                await _host.Batteries.RefreshAsync();
            });
        }
        finally
        {
            IsScanning = false;
            RefreshDevices();
        }
    }

    partial void OnEnabledChanged(bool value)
    {
        if (!_syncing) _host.Lights.SetEnabled(value);
    }

    partial void OnBrightnessChanged(double value)
    {
        if (!_syncing) _host.Lights.SetBrightness(value / 100);
    }

    partial void OnFpsChanged(int value) => _host.SetFps(value);

    partial void OnStartMinimizedChanged(bool value)
    {
        _host.Settings.StartMinimized = value;
        _host.ScheduleSave();
    }

    partial void OnStartWithWindowsChanged(bool value) => StartupRegistration.Set(value);

    partial void OnOpenRgbPathChanged(string value)
    {
        _host.Settings.OpenRgb.ExecutablePath = string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"');
        _host.ScheduleSave();
    }

    private void SyncState()
    {
        var s = _host.Lights.State;
        _syncing = true;
        try
        {
            Enabled = s.Enabled;
            Brightness = Math.Round(s.Brightness * 100);
            ActivePresetName = s.ActivePresetName ?? "None";
            Paused = s.Paused;
        }
        finally
        {
            _syncing = false;
        }
        foreach (var p in Presets) p.IsActive = p.Id == s.ActivePresetId;
        // The Studio's unsaved draft has no card; keep showing the last preset's colors for it.
        ActiveCard = Presets.FirstOrDefault(p => p.IsActive);
        if (Presets.FirstOrDefault(p => p.IsActive) is { } active) ActiveSwatch = active.Swatch;
        else if (ActiveSwatch is null && Application.Current?.TryGetResource("RgbGradient", null, out var gradient) == true)
            ActiveSwatch = gradient as IBrush;
        RefreshAlerts();
    }

    private void RefreshPresets()
    {
        Presets.Clear();
        foreach (var p in _host.Lights.GetPresets()) Presets.Add(new PresetCardViewModel(p, this));
        QuickPresets.Clear();
        foreach (var p in Presets.Take(8)) QuickPresets.Add(p);
        SyncState();
    }

    private void RefreshDevices()
    {
        var devices = _host.Devices.Devices;
        var names = DeviceNames.Unique(devices);
        var excluded = _host.Settings.ExcludedDevices.ToHashSet(StringComparer.OrdinalIgnoreCase);

        Devices.Clear();
        foreach (var d in devices.OrderBy(d => d.Leds.Count == 0).ThenBy(d => DisplayOrder(d.Type)).ThenBy(d => names[d.Id]))
            Devices.Add(new DeviceRowViewModel(d, names[d.Id], !excluded.Contains(d.Id),
                (id, inc) => { _host.SetExcluded(id, !inc); RefreshAlerts(); }, _host.SetZoneSizeAsync,
                id => _host.Lights.Flash(id)));
        Studio.SetDevices(devices.Select(d => (d.Id, names[d.Id])));

        int controlled = Devices.Count(d => d.Included && !d.NeedsSetup);
        HomeSummary = Devices.Count == 0 ? "Looking for your devices…"
            : controlled == Devices.Count ? $"MyRGB is lighting all {Devices.Count} of your devices."
            : $"MyRGB is lighting {controlled} of your {Devices.Count} devices.";
        ProviderStatus = string.Join("   ", _host.Devices.Providers.Select(p => $"{p.DisplayName}: {p.StatusMessage ?? p.Status.ToString()}"));
        OnPropertyChanged(nameof(HasDevices));

        ApplyBatteries();
        if (Vendors.CheckedOnce) _ = Vendors.Check(); // readiness depends on which devices are controlled
    }

    /// <summary>The things people touch first, then the PC's insides.</summary>
    private static int DisplayOrder(DeviceType type) => type switch
    {
        DeviceType.Keyboard => 0,
        DeviceType.Mouse => 1,
        DeviceType.Headset => 2,
        DeviceType.HeadsetStand => 3,
        DeviceType.Keypad or DeviceType.MouseMat or DeviceType.Speaker or DeviceType.Microphone or DeviceType.Gamepad => 4,
        DeviceType.Monitor or DeviceType.Light => 5,
        DeviceType.LedStrip or DeviceType.Cooler or DeviceType.Case => 6,
        DeviceType.Gpu => 7,
        DeviceType.Motherboard => 8,
        DeviceType.Memory => 9,
        _ => 10,
    };

    private void ApplyBatteries()
    {
        var readings = _host.Batteries.Readings;
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Devices)
        {
            var reading = row.HardwareKey is { } key ? readings.GetValueOrDefault(key) : null;
            row.ApplyBattery(reading);
            if (reading is not null) matched.Add(reading.HardwareKey);
        }

        OtherBatteries.Clear();
        foreach (var r in readings.Values.Where(r => !matched.Contains(r.HardwareKey)).DistinctBy(r => r.Name))
            OtherBatteries.Add(new BatteryOnlyViewModel(r.Name, r.Status));
        OnPropertyChanged(nameof(HasOtherBatteries));
        RefreshAlerts();
    }

    private void RefreshAlerts()
    {
        Alerts.Clear();

        if (!_host.Lights.State.Enabled)
            Alerts.Add(new AlertViewModel(false, "Your lights are switched off.", "Turn on", TogglePowerCommand));

        if (_host.DynamicLightingOn() == true)
            Alerts.Add(new AlertViewModel(true,
                "Windows Dynamic Lighting is on. Windows may take over some keyboards and mice and fight MyRGB for them. Turn off \"Use Dynamic Lighting on my devices\".",
                "Open Windows settings", OpenDynamicLightingCommand));

        foreach (var app in Vendors.Apps.Where(a => a.Running))
            Alerts.Add(new AlertViewModel(true, $"{app.Name} is running. It can override MyRGB's lighting on its devices.", "Vendor apps", NavigateCommand.WithParameter("vendors")));
        int idle = Vendors.Apps.Count(a => !a.Running);
        if (idle > 0)
            Alerts.Add(new AlertViewModel(false,
                idle == 1 ? $"{Vendors.Apps.First(a => !a.Running).Name} is still installed. MyRGB may be able to replace it." : $"{idle} vendor lighting apps are still installed. MyRGB may be able to replace them.",
                "Review", NavigateCommand.WithParameter("vendors")));

        foreach (var d in Devices.Where(d => d.NeedsSetup && d.Included))
            Alerts.Add(new AlertViewModel(true, $"{d.Name} needs its LED count set before it can light up.", "Set up", NavigateCommand.WithParameter("devices")));

        foreach (var d in Devices.Where(d => d.Battery is { Charging: false } b && b.Percent < LowBattery))
            Alerts.Add(new AlertViewModel(true, $"{d.Name} battery is low ({d.BatteryText}). Time to charge it."));

        OnPropertyChanged(nameof(HasAlerts));
    }
}

internal static class CommandExtensions
{
    /// <summary>Bind a parameter now, so an alert's button can carry "go to the Vendor apps page".</summary>
    public static IRelayCommand WithParameter(this IRelayCommand<string?> command, string parameter) =>
        new RelayCommand(() => command.Execute(parameter));
}
