using System.Collections.ObjectModel;
using System.IO;
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
    [RelayCommand] private void Export() => owner.ExportPreset(Id);

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
    public IBrush Accent => new SolidColorBrush(Warning ? ThemeManager.Palette.Warning : ThemeManager.Palette.Info);
    public bool HasAction => ActionLabel is not null;
}

public sealed partial class MainViewModel : ObservableObject
{
    // Sidebar order. Keep in sync with the TabItems in MainWindow.axaml.
    public const int HomeTab = 0, PresetsTab = 1, CommunityTab = 2, StudioTab = 3, WallpapersTab = 4, DevicesTab = 5, MonitoringTab = 6, SettingsTab = 7;

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
        Update = new UpdateViewModel(host);
        Wallpapers = new WallpapersViewModel(host);
        _fps = host.Engine.Fps;
        _startMinimized = host.Settings.StartMinimized;
        _checkForUpdates = host.Settings.CheckForUpdates;
        _appProfilesOn = host.Settings.AppProfilesOn;
        _idleDimOn = host.Settings.IdleDimOn;
        _idleDimAfterMinutes = host.Settings.IdleDimAfterMinutes;
        _idleDimPercent = host.Settings.IdleDimPercent;
        _globalHotkeyOn = host.Settings.GlobalHotkeyOn;
        _devicesGrid = host.Settings.DevicesGrid;
        host.HotkeysChanged += () => Dispatcher.UIThread.Post(SyncHotkeys);
        SyncHotkeys();
        _offWhenLocked = host.Settings.OffWhenLocked;
        _nightOn = host.Settings.NightOn;
        var night = LightsOffWatcher.NightFor(host.Settings);
        _nightFrom = night.From.ToString("HH:mm");
        _nightTo = night.To.ToString("HH:mm");
        foreach (var p in host.Settings.AppProfiles) AppProfileRows.Add(new AppProfileRow(host, p, this));
        _scheduleOn = host.Settings.ScheduleOn;
        foreach (var e in host.Settings.Schedule) ScheduleRows.Add(new ScheduleRow(e, this));
        _monitorsOn = host.Settings.MonitorsOn;
        _monitorLevel = host.Settings.MonitorLevel ?? host.Monitors.Level;
        _monitorsFollowLights = host.Settings.MonitorsFollowLights;
        _monitorsOffLevel = host.Settings.MonitorsOffLevel;
        _monitorsNightOn = host.Settings.MonitorsNightOn;
        _monitorsNightLevel = host.Settings.MonitorsNightLevel;
        _monitorPresetLevelsOn = host.Settings.MonitorPresetLevelsOn;
        foreach (var m in host.Settings.MonitorPresetLevels) MonitorPresetRows.Add(new MonitorPresetRow(m, this));
        host.Monitors.Changed += () => Dispatcher.UIThread.Post(SyncMonitors);
        SyncMonitors();
        _showHomeGuide = !host.Settings.HideHomeGuide;
        _startWithWindows = StartupRegistration.IsEnabled;
        _openRgbPath = host.Settings.OpenRgb.ExecutablePath ?? "";
        Themes.Add(ThemeOptionViewModel.Auto(PickTheme));
        foreach (var t in ThemeManager.All) Themes.Add(new ThemeOptionViewModel(t, PickTheme));
        foreach (var t in Themes) t.IsSelected = t.Id == ThemeManager.RequestedId;

        host.Lights.StateChanged += (_, _) => Dispatcher.UIThread.Post(SyncState);
        host.Presets.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshPresets);
        host.Devices.DevicesChanged += (_, _) => Dispatcher.UIThread.Post(RefreshDevices);
        host.Batteries.Changed += (_, _) => Dispatcher.UIThread.Post(ApplyBatteries);
        Vendors.Updated += RefreshAlerts;
        host.Sensors.Updated += () => Dispatcher.UIThread.Post(RefreshSensors);
        host.HealthChanged += () => Dispatcher.UIThread.Post(RefreshAlerts);

        RefreshPresets();
        RefreshDevices();
        SyncState();
        _ = Vendors.Check();

        WhatsNewLines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowWhatsNew));
        bool firstRun = !host.Settings.HasSeenWelcome;
        Version? seen = Version.TryParse(host.Settings.LastSeenVersion, out var v) ? v : null;
        foreach (var release in WhatsNew.Since(seen, CurrentVersion, firstRun))
            foreach (var line in release.Lines) WhatsNewLines.Add(line);
        if (WhatsNewLines.Count == 0) RememberVersionSeen();
        if (firstRun) OpenTour();

        // First look a minute in, once the account has said whether this is a supporter; then hourly,
        // since MyRGB usually runs for days at a time.
        var nudge = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        nudge.Tick += (_, _) =>
        {
            nudge.Interval = TimeSpan.FromHours(1);
            SupportNudge.Check(DateTime.Now);
        };
        nudge.Start();

        // The update check comes a little later, once the desk is already lit, then runs hourly
        // while MyRGB is open (it usually runs for days); its strip only appears when a newer
        // release is actually out.
        var updateCheck = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        updateCheck.Tick += (_, _) =>
        {
            Update.CheckOnStart();
            updateCheck.Interval = TimeSpan.FromHours(1);
        };
        updateCheck.Start();
    }

    public AppHost Host => _host;
    public StudioViewModel Studio { get; }
    public WallpapersViewModel Wallpapers { get; }
    public VendorAppsViewModel Vendors { get; }
    public AccountViewModel Account { get; }
    public CommunityViewModel Community { get; }
    public MonitoringViewModel Monitoring { get; }
    public CoolingViewModel Cooling { get; }
    public SupportNudgeViewModel SupportNudge { get; }
    public UpdateViewModel Update { get; }

    /// <summary>Sensor pages only redraw while they're showing; the hub keeps reading either way.</summary>
    private void RefreshSensors()
    {
        // Home shows a summary of both.
        if (SelectedTab is HomeTab || (SelectedTab is MonitoringTab && !ShowCooling)) Monitoring.Refresh();
        if (SelectedTab is HomeTab || (SelectedTab is MonitoringTab && ShowCooling)) Cooling.Refresh();
    }

    /// <summary>Builds without an account service hide Community and the account panels.</summary>
    public bool CloudAvailable => _host.Cloud.IsConfigured;

    partial void OnSelectedTabChanged(int value)
    {
        if (value == CommunityTab && !Community.LoadedOnce) _ = Community.Load();
        if (value is MonitoringTab) RefreshSensors();
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

    // ---- What's new: a card on Home after an update, until dismissed ----

    public static Version CurrentVersion { get; } = WhatsNew.Trim(typeof(MainViewModel).Assembly.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>What changed since the version last seen, newest first. Empty when there's nothing to say.</summary>
    public ObservableCollection<string> WhatsNewLines { get; } = [];
    public bool ShowWhatsNew => WhatsNewLines.Count > 0;
    public string WhatsNewTitle => $"New in MyRGB {CurrentVersion.ToString(3)}";

    [RelayCommand]
    private void DismissWhatsNew()
    {
        WhatsNewLines.Clear();
        OnPropertyChanged(nameof(ShowWhatsNew));
        RememberVersionSeen();
    }

    [RelayCommand]
    private static void OpenReleaseNotes() =>
        AccountViewModel.OpenLink($"https://github.com/kylechrisking/MyRGB/releases/tag/v{CurrentVersion.ToString(3)}-beta");

    private void RememberVersionSeen()
    {
        if (_host.Settings.LastSeenVersion == CurrentVersion.ToString(3)) return;
        _host.Settings.LastSeenVersion = CurrentVersion.ToString(3);
        _host.ScheduleSave();
    }

    /// <summary>Settings' Appearance picker.</summary>
    public ObservableCollection<ThemeOptionViewModel> Themes { get; } = [];

    /// <summary>Switch the look live, remember it, and repaint what C# draws itself.</summary>
    public void PickTheme(string id)
    {
        if (Avalonia.Application.Current is not { } app) return;
        ThemeManager.Apply(app, id);
        _host.Settings.Theme = ThemeManager.RequestedId;
        _host.ScheduleSave();
        foreach (var t in Themes) t.IsSelected = t.Id == ThemeManager.RequestedId;
        RefreshAlerts();
        RefreshDevices();
    }

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
    public string PlayingLabel => Paused ? "Lighting · paused" : "Lighting";
    [ObservableProperty] private IBrush? _activeSwatch;
    [ObservableProperty] private string _providerStatus = "Looking for your devices…";
    [ObservableProperty] private string _homeSummary = "Looking for your devices…";
    [ObservableProperty] private int _fps;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _checkForUpdates;
    [ObservableProperty] private bool _appProfilesOn;
    [ObservableProperty] private bool _idleDimOn;
    [ObservableProperty] private double _idleDimAfterMinutes;
    [ObservableProperty] private double _idleDimPercent;
    [ObservableProperty] private string _newAppProfileProcess = "";
    [ObservableProperty] private bool _globalHotkeyOn;
    [ObservableProperty] private IReadOnlyList<HotkeyStatus> _hotkeys = [];
    [ObservableProperty] private string _hotkeysNote = "";

    private void SyncHotkeys()
    {
        Hotkeys = _host.Hotkeys;
        var taken = Hotkeys.Where(h => h.Taken).Select(h => h.Keys).ToList();
        HotkeysNote = taken.Count == 0 ? "Work from any app, no vendor hotkey engine needed. The brightness keys repeat while you hold them."
            : $"{string.Join(", ", taken)} {(taken.Count == 1 ? "is" : "are")} already used by another app, so MyRGB can't have {(taken.Count == 1 ? "it" : "them")}. The rest work.";
    }
    [ObservableProperty] private bool _offWhenLocked;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NightTimesEnabled))]
    private bool _nightOn;
    /// <summary>The night times also drive the monitors' night level.</summary>
    public bool NightTimesEnabled => NightOn || MonitorsNightOn;
    [ObservableProperty] private string _nightFrom;
    [ObservableProperty] private string _nightTo;
    public IReadOnlyList<string> NightChoices => NightHours.Choices;

    partial void OnOffWhenLockedChanged(bool value) => SaveLightsOff(s => s.OffWhenLocked = value);
    partial void OnNightOnChanged(bool value) => SaveLightsOff(s => s.NightOn = value);
    partial void OnNightFromChanged(string value) { if (value is not null) SaveLightsOff(s => s.NightFrom = value); }
    partial void OnNightToChanged(string value) { if (value is not null) SaveLightsOff(s => s.NightTo = value); }

    private void SaveLightsOff(Action<AppSettings> change)
    {
        change(_host.Settings);
        _host.ScheduleSave();
        _host.CheckLightsOff();
    }

    // ---- Monitors (DDC/CI) -------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMonitorSlider))]
    private bool _monitorsOn;
    [ObservableProperty] private double _monitorLevel;
    [ObservableProperty] private bool _monitorsFollowLights;
    [ObservableProperty] private int _monitorsOffLevel;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NightTimesEnabled))]
    private bool _monitorsNightOn;
    [ObservableProperty] private int _monitorsNightLevel;
    [ObservableProperty] private bool _monitorPresetLevelsOn;
    [ObservableProperty] private string _monitorStatus = "";
    public ObservableCollection<MonitorPresetRow> MonitorPresetRows { get; } = [];
    public ObservableCollection<MonitorRangeRow> MonitorRangeRows { get; } = [];
    public bool ShowMonitorRanges => MonitorRangeRows.Count > 0;
    public IReadOnlyList<int> MonitorSteps => MonitorLevels.Steps;
    public bool MonitorsSupported => _host.Monitors.Installed;
    public bool ShowMonitorSlider => MonitorsOn && MonitorsSupported;
    private bool _syncingMonitors;

    partial void OnMonitorsOnChanged(bool value)
    {
        _host.Settings.MonitorsOn = value;
        _host.ScheduleSave();
        if (value) _ = _host.Monitors.RefreshAsync();
        else _host.Monitors.SettingsChanged();
        SyncMonitors();
    }

    partial void OnMonitorLevelChanged(double value)
    {
        if (!_syncingMonitors) _host.Monitors.SetLevel((int)Math.Round(value));
    }

    partial void OnMonitorsFollowLightsChanged(bool value) => SaveMonitors(s => s.MonitorsFollowLights = value);
    partial void OnMonitorsOffLevelChanged(int value) => SaveMonitors(s => s.MonitorsOffLevel = value);
    partial void OnMonitorsNightOnChanged(bool value) => SaveMonitors(s => s.MonitorsNightOn = value);
    partial void OnMonitorsNightLevelChanged(int value) => SaveMonitors(s => s.MonitorsNightLevel = value);
    partial void OnMonitorPresetLevelsOnChanged(bool value) => SaveMonitors(s => s.MonitorPresetLevelsOn = value);

    private void SaveMonitors(Action<AppSettings> change)
    {
        change(_host.Settings);
        _host.ScheduleSave();
        _host.Monitors.Update();
    }

    [RelayCommand]
    private void AddMonitorPresetLevel()
    {
        string presetId = _host.Lights.State.ActivePresetId ?? _host.Lights.GetPresets().FirstOrDefault()?.Id ?? "rainbow-wave";
        var entry = new PresetMonitorLevel(presetId, 50);
        _host.Settings.MonitorPresetLevels.Add(entry);
        MonitorPresetRows.Add(new MonitorPresetRow(entry, this));
        SaveMonitors(_ => { });
    }

    internal void RemoveMonitorPresetLevel(MonitorPresetRow row)
    {
        _host.Settings.MonitorPresetLevels.Remove(row.Entry);
        MonitorPresetRows.Remove(row);
        SaveMonitors(_ => { });
    }

    internal void UpdateMonitorPresetLevel(MonitorPresetRow row, PresetMonitorLevel updated)
    {
        int idx = _host.Settings.MonitorPresetLevels.IndexOf(row.Entry);
        if (idx >= 0) _host.Settings.MonitorPresetLevels[idx] = updated;
        row.Entry = updated;
        SaveMonitors(_ => { });
    }

    internal void UpdateMonitorRange(MonitorRange range)
    {
        var list = _host.Settings.MonitorRanges;
        list.RemoveAll(r => string.Equals(r.Name, range.Name, StringComparison.OrdinalIgnoreCase));
        if (range.Darkest != 0 || range.Brightest != 100) list.Add(range);
        _host.ScheduleSave();
        _host.Monitors.RangesChanged();
    }

    private void SyncMonitors()
    {
        var m = _host.Monitors;
        _syncingMonitors = true;
        try { MonitorLevel = m.Level; }
        finally { _syncingMonitors = false; }
        if (!m.Monitors.SequenceEqual(MonitorRangeRows.Select(r => r.Name)))
        {
            MonitorRangeRows.Clear();
            foreach (var name in m.Monitors) MonitorRangeRows.Add(new MonitorRangeRow(m.RangeFor(name) ?? new MonitorRange(name), this));
            OnPropertyChanged(nameof(ShowMonitorRanges));
        }
        MonitorStatus = !m.Installed ? "Monitor brightness needs Windows."
            : !MonitorsOn ? "MyRGB talks to your monitors over DDC/CI, the control line in the display cable; nothing else to install. Switch this on to let it set their brightness."
            : m.Problem is { } p ? p
            : m.Monitors.Count == 0 ? "Looking for your monitors…"
            : $"Controlling {string.Join(" and ", m.Monitors)}.";
    }
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

    [ObservableProperty] private string _importPresetPath = "";
    [ObservableProperty] private string? _presetIoStatus;

    public void ExportPreset(string id)
    {
        if (_host.Presets.Find(id) is not { } preset) return;
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MyRGB Presets");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, PresetFile.FileName(preset.Name));
            PresetFile.Export(preset, path);
            PresetIoStatus = $"Exported \"{preset.Name}\" to {path}";
        }
        catch (Exception ex) { PresetIoStatus = $"Couldn't export \"{preset.Name}\": {ex.Message}"; }
    }

    [RelayCommand]
    private void ImportPreset()
    {
        var path = ImportPresetPath.Trim().Trim('"');
        if (path.Length == 0) return;
        try
        {
            var preset = PresetFile.Import(path);
            var stored = _host.Presets.Save(preset with { Id = PresetLibrary.NewId(preset.Name) });
            ImportPresetPath = "";
            PresetIoStatus = $"Imported \"{stored.Name}\".";
        }
        catch (Exception ex) { PresetIoStatus = $"Couldn't import that file: {ex.Message}"; }
    }

    /// <summary>Jump to a page by name; used by Home's buttons and the tour. Old page names still work.</summary>
    [RelayCommand]
    private void Navigate(string? page)
    {
        if (page is "layout") ArrangingLayout = true;
        if (page is "cooling") ShowCooling = true;
        else if (page is "monitoring") ShowCooling = false;
        SelectedTab = page switch
        {
            "home" => HomeTab,
            "presets" => PresetsTab,
            "studio" or "layout" => StudioTab,
            "wallpapers" or "wallpaper" => WallpapersTab,
            "devices" => DevicesTab,
            "monitoring" or "cooling" => MonitoringTab,
            "community" => CommunityTab,
            "settings" or "account" or "vendors" => SettingsTab,
            _ => SelectedTab,
        };
    }

    /// <summary>Devices page: tiles with a lit drawing of each device, or the detailed list.</summary>
    [ObservableProperty] private bool _devicesGrid;

    partial void OnDevicesGridChanged(bool value)
    {
        _host.Settings.DevicesGrid = value;
        _host.ScheduleSave();
    }

    [RelayCommand]
    private void SetDevicesGrid(string? on) => DevicesGrid = on == "True";

    /// <summary>Studio's live preview doubles as the layout editor while this is on.</summary>
    [ObservableProperty] private bool _arrangingLayout;

    /// <summary>Monitoring shows Cooling (temperatures and fans) instead of the live readings.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonitoringIntro))]
    private bool _showCooling;

    public string MonitoringIntro => ShowCooling
        ? "Temperatures, and the fans MyRGB drives for you. Give any fan a fixed speed or a curve; anything you leave on Automatic runs on its hardware default."
        : "Live readings from your PC, once a second, with the last five minutes charted. Nothing leaves this computer.";

    [RelayCommand]
    private void SetShowCooling(string? on) => ShowCooling = on == "True";

    partial void OnShowCoolingChanged(bool value) => RefreshSensors();

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

    /// <summary>Re-register the elevated OpenRGB task from an admin-only copy of OpenRGB: one Windows prompt.</summary>
    [RelayCommand]
    private void FixOpenRgbTask()
    {
        string script = Path.Combine(AppContext.BaseDirectory, "setup", "setup-openrgb-admin.ps1");
        string bundled = Path.Combine(AppContext.BaseDirectory, "OpenRGB", "OpenRGB.exe");
        string? source = File.Exists(bundled) ? bundled : _host.UnsafeOpenRgbTask();
        if (!File.Exists(script) || source is null) return;
        bool asked = Program.Elevate("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" " +
            $"-OpenRgbPath \"{source}\" -CopyTo \"{MyRGB.Providers.OpenRgb.ElevatedTask.SafeOpenRgbDir}\" -User \"{Environment.UserDomainName}\\{Environment.UserName}\"");
        if (!asked) return;
        _host.ForgetOpenRgbTask();
        // The script takes a few seconds; look again so the alert goes once it's done.
        _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ => Dispatcher.UIThread.Post(() => { _host.ForgetOpenRgbTask(); RefreshAlerts(); }));
    }

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
                await _host.Devices.RescanHardwareAsync(); // OpenRGB looks again too, and reports back when done
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

    partial void OnCheckForUpdatesChanged(bool value)
    {
        _host.Settings.CheckForUpdates = value;
        _host.ScheduleSave();
    }

    partial void OnAppProfilesOnChanged(bool value)
    {
        _host.Settings.AppProfilesOn = value;
        _host.ScheduleSave();
    }

    partial void OnIdleDimOnChanged(bool value)
    {
        _host.Settings.IdleDimOn = value;
        _host.ScheduleSave();
    }

    partial void OnGlobalHotkeyOnChanged(bool value) => _host.SetGlobalHotkeyEnabled(value);

    partial void OnIdleDimAfterMinutesChanged(double value)
    {
        _host.Settings.IdleDimAfterMinutes = Math.Round(value);
        _host.ScheduleSave();
    }

    partial void OnIdleDimPercentChanged(double value)
    {
        _host.Settings.IdleDimPercent = Math.Round(value);
        _host.ScheduleSave();
    }

    public ObservableCollection<AppProfileRow> AppProfileRows { get; } = [];

    [RelayCommand]
    private void AddAppProfile()
    {
        var profile = new AppProfile(NewAppProfileProcess.Trim(), _host.Lights.GetPresets().FirstOrDefault()?.Id ?? "rainbow-wave");
        if (AppProfileRows.Any(r => r.ProcessName.Equals(profile.ProcessName, StringComparison.OrdinalIgnoreCase))) return;
        _host.Settings.AppProfiles.Add(profile);
        AppProfileRows.Add(new AppProfileRow(_host, profile, this));
        NewAppProfileProcess = "";
        _host.ScheduleSave();
    }

    internal void RemoveAppProfile(AppProfileRow row)
    {
        _host.Settings.AppProfiles.Remove(row.Profile);
        AppProfileRows.Remove(row);
        _host.ScheduleSave();
    }

    internal void UpdateProfileProcess(AppProfileRow row, string process)
    {
        var updated = row.Profile with { ProcessName = process.Trim() };
        var idx = _host.Settings.AppProfiles.IndexOf(row.Profile);
        if (idx >= 0) _host.Settings.AppProfiles[idx] = updated;
        row.Profile = updated;
        _host.ScheduleSave();
    }

    internal void UpdateProfilePreset(AppProfileRow row, string presetId)
    {
        var updated = row.Profile with { PresetId = presetId };
        var idx = _host.Settings.AppProfiles.IndexOf(row.Profile);
        if (idx >= 0) _host.Settings.AppProfiles[idx] = updated;
        row.Profile = updated;
        _host.ScheduleSave();
    }

    [ObservableProperty] private bool _scheduleOn;
    public ObservableCollection<ScheduleRow> ScheduleRows { get; } = [];

    partial void OnScheduleOnChanged(bool value)
    {
        _host.Settings.ScheduleOn = value;
        ScheduleChanged();
    }

    [RelayCommand]
    private void AddScheduleEntry()
    {
        // Start a new row an hour after the last one, so adding a few in a row gives distinct times.
        var last = ScheduleRows.Select(r => r.Entry.Time).DefaultIfEmpty(new TimeOnly(17, 0)).Max();
        var entry = new ScheduledPreset(last.AddHours(1).ToString("HH:00"), _host.Lights.State.ActivePresetId ?? _host.Lights.GetPresets().FirstOrDefault()?.Id ?? "rainbow-wave");
        _host.Settings.Schedule.Add(entry);
        ScheduleRows.Add(new ScheduleRow(entry, this));
        ScheduleChanged();
    }

    internal void RemoveScheduleEntry(ScheduleRow row)
    {
        _host.Settings.Schedule.Remove(row.Entry);
        ScheduleRows.Remove(row);
        ScheduleChanged();
    }

    internal void UpdateScheduleEntry(ScheduleRow row, ScheduledPreset updated)
    {
        int idx = _host.Settings.Schedule.IndexOf(row.Entry);
        if (idx >= 0) _host.Settings.Schedule[idx] = updated;
        row.Entry = updated;
        ScheduleChanged();
    }

    private void ScheduleChanged()
    {
        _host.ScheduleSave();
        _host.CheckScheduleNow();
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
                id => _host.Lights.Flash(id), d is IScreenDevice s ? new ScreenViewModel(s, _host) : null));
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

        if (_host.Lights.State.HeldBy is { } heldBy)
            Alerts.Add(new AlertViewModel(false, $"Your lights are off for now: {heldBy}.", "Turn on anyway", TogglePowerCommand));
        else if (!_host.Lights.State.Enabled)
            Alerts.Add(new AlertViewModel(false, "Your lights are switched off.", "Turn on", TogglePowerCommand));

        if (_host.UnsafeOpenRgbTask() is not null)
            Alerts.Add(new AlertViewModel(true,
                "The task that runs OpenRGB as administrator for your RAM lighting starts it from your own user folder, where any program could swap it. One Windows prompt moves it into Program Files, where only administrators can change it.",
                "Fix it", FixOpenRgbTaskCommand));
        else if (_host.OpenRgbTask() is { ViaVbScript: true })
            Alerts.Add(new AlertViewModel(false,
                "The task that starts OpenRGB for your RAM lighting still goes through a VBScript file, and Windows is removing VBScript. One Windows prompt switches it to start OpenRGB directly, in the background.",
                "Update it", FixOpenRgbTaskCommand));

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

        if (_host.SaveError is { } saveError)
            Alerts.Add(new AlertViewModel(true,
                $"MyRGB can't save its settings ({saveError.TrimEnd('.')}). Changes you make now will be lost when it closes. Quitting and starting it again usually fixes this."));
        if (_host.LogFile.WriteError is { } logError)
            Alerts.Add(new AlertViewModel(false,
                $"MyRGB can't write its log file ({logError.TrimEnd('.')}), so it's writing to {FileLoggerProvider.FallbackPath} instead."));

        foreach (var d in Devices.Where(d => d.Battery is { Charging: false } b && b.Percent < LowBattery))
            Alerts.Add(new AlertViewModel(true, $"{d.Name} battery is low ({d.BatteryText}). Time to charge it."));

        OnPropertyChanged(nameof(HasAlerts));
    }
}

internal static class CommandExtensions
{
    /// <summary>Bind a parameter now, so an alert's button can carry "go to the vendor apps in Settings".</summary>
    public static IRelayCommand WithParameter(this IRelayCommand<string?> command, string parameter) =>
        new RelayCommand(() => command.Execute(parameter));
}

/// <summary>One program-to-preset mapping on the Settings page.</summary>
public sealed partial class AppProfileRow : ObservableObject
{
    private readonly MainViewModel _owner;

    public AppProfileRow(AppHost host, AppProfile profile, MainViewModel owner)
    {
        _owner = owner;
        Profile = profile;
        _processName = profile.ProcessName;
        _presetId = profile.PresetId;
    }

    public AppProfile Profile { get; internal set; }

    public ObservableCollection<PresetCardViewModel> AvailablePresets => _owner.Presets;

    [ObservableProperty] private string _processName;
    [ObservableProperty] private string _presetId;

    partial void OnProcessNameChanged(string value) => _owner.UpdateProfileProcess(this, value);
    partial void OnPresetIdChanged(string value) => _owner.UpdateProfilePreset(this, value);

    [RelayCommand]
    private void Remove() => _owner.RemoveAppProfile(this);
}

public sealed partial class ScheduleRow : ObservableObject
{
    private readonly MainViewModel _owner;

    public ScheduleRow(ScheduledPreset entry, MainViewModel owner)
    {
        _owner = owner;
        Entry = entry;
        _at = entry.Time.ToString("HH:mm");
        _presetId = entry.PresetId;
    }

    public ScheduledPreset Entry { get; internal set; }

    public IReadOnlyList<string> Times => NightHours.Choices;
    public ObservableCollection<PresetCardViewModel> AvailablePresets => _owner.Presets;

    [ObservableProperty] private string _at;
    [ObservableProperty] private string _presetId;

    partial void OnAtChanged(string value) { if (value is not null) _owner.UpdateScheduleEntry(this, Entry with { At = value }); }
    partial void OnPresetIdChanged(string value) { if (value is not null) _owner.UpdateScheduleEntry(this, Entry with { PresetId = value }); }

    [RelayCommand]
    private void Remove() => _owner.RemoveScheduleEntry(this);
}

public sealed partial class MonitorPresetRow : ObservableObject
{
    private readonly MainViewModel _owner;

    public MonitorPresetRow(PresetMonitorLevel entry, MainViewModel owner)
    {
        _owner = owner;
        Entry = entry;
        _presetId = entry.PresetId;
        _level = entry.Level;
    }

    public PresetMonitorLevel Entry { get; internal set; }

    public ObservableCollection<PresetCardViewModel> AvailablePresets => _owner.Presets;
    public IReadOnlyList<int> Steps => MonitorLevels.Steps;

    [ObservableProperty] private string _presetId;
    [ObservableProperty] private int _level;

    partial void OnPresetIdChanged(string value) { if (value is not null) _owner.UpdateMonitorPresetLevel(this, Entry with { PresetId = value }); }
    partial void OnLevelChanged(int value) => _owner.UpdateMonitorPresetLevel(this, Entry with { Level = value });

    [RelayCommand]
    private void Remove() => _owner.RemoveMonitorPresetLevel(this);
}

/// <summary>One monitor's own span for the shared level, so a brighter panel can be matched to a dimmer one.</summary>
public sealed partial class MonitorRangeRow : ObservableObject
{
    private readonly MainViewModel _owner;

    public MonitorRangeRow(MonitorRange range, MainViewModel owner)
    {
        _owner = owner;
        Name = range.Name;
        _darkest = range.Darkest;
        _brightest = range.Brightest;
    }

    public string Name { get; }
    public static IReadOnlyList<int> DarkestSteps { get; } = [0, 5, 10, 15, 20, 25, 30, 40, 50];
    public static IReadOnlyList<int> BrightestSteps { get; } = [100, 95, 90, 85, 80, 75, 70, 60, 50];
    public IReadOnlyList<int> DarkestChoices => DarkestSteps.Contains(Darkest) ? DarkestSteps : [.. DarkestSteps, Darkest];
    public IReadOnlyList<int> BrightestChoices => BrightestSteps.Contains(Brightest) ? BrightestSteps : [.. BrightestSteps, Brightest];

    [ObservableProperty] private int _darkest;
    [ObservableProperty] private int _brightest;

    partial void OnDarkestChanged(int value) => _owner.UpdateMonitorRange(new MonitorRange(Name, value, Brightest));
    partial void OnBrightestChanged(int value) => _owner.UpdateMonitorRange(new MonitorRange(Name, Darkest, value));
}
