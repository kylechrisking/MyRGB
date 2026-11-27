using System.Text.Json;
using MyRGB.Api;
using MyRGB.Core;
using MyRGB.Core.Devices;
using MyRGB.Core.Layout;
using MyRGB.Core.Presets;
using MyRGB.Core.Sensors;
using MyRGB.Providers.OpenRgb;

namespace MyRGB.App.Services;

/// <summary>Everything MyRGB remembers between runs, stored as %APPDATA%\MyRGB\settings.json.</summary>
public sealed class AppSettings
{
    public string? ActivePresetId { get; set; } = "rainbow-wave";
    public double Brightness { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public int Fps { get; set; } = 30;
    public bool StartMinimized { get; set; }

    /// <summary>Look for a newer MyRGB release when it starts.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>Show the Cooling page's fans as compact tiles instead of full rows.</summary>
    public bool CoolingGrid { get; set; }

    /// <summary>Keep presets in sync across PCs while signed in.</summary>
    public bool SyncPresets { get; set; } = true;

    /// <summary>False until the first-run tour has been finished or skipped.</summary>
    public bool HasSeenWelcome { get; set; }

    /// <summary>The version whose "What's new" card was last dismissed (see MyRGB.Core.WhatsNew).</summary>
    public string? LastSeenVersion { get; set; }

    /// <summary>The "How MyRGB works" card on Home was hidden.</summary>
    public bool HideHomeGuide { get; set; }

    /// <summary>Different days MyRGB has been used on; the support note waits for a week of them.</summary>
    public int DaysUsed { get; set; }
    public DateOnly? LastUsedDay { get; set; }

    /// <summary>When the support note was last answered, and whether it was told never to ask again.</summary>
    public DateTime? SupportAskedAt { get; set; }
    public bool SupportAskOff { get; set; }
    public int ApiPort { get; set; } = LocalApiServer.DefaultPort;
    public List<Preset> UserPresets { get; set; } = [];
    public List<DevicePlacement> Layout { get; set; } = [];
    public List<string> ExcludedDevices { get; set; } = [];
    public List<ZoneSize> ZoneSizes { get; set; } = [];

    /// <summary>Devices whose screen should show (they run their own lighting meanwhile); see <see cref="IScreenDevice.ScreenFirst"/>.</summary>
    public List<string> ScreenFirstDevices { get; set; } = [];

    /// <summary>Fans the user set to a fixed speed or a curve. Fans not listed are left to the hardware.</summary>
    public List<FanSetting> Fans { get; set; } = [];

    /// <summary>What each fan test found: top RPM, and where the fan stops and starts again.</summary>
    public List<FanCalibration> FanCalibrations { get; set; } = [];

    /// <summary>Curve shapes named and saved from the curve builder, reusable on any fan.</summary>
    public List<SavedCurve> SavedCurves { get; set; } = [];

    /// <summary>Switch presets when a program takes the foreground.</summary>
    public bool AppProfilesOn { get; set; }
    public List<AppProfile> AppProfiles { get; set; } = [];

    /// <summary>After so many minutes without input, dim the lights until you're back.</summary>
    public bool IdleDimOn { get; set; }
    public double IdleDimAfterMinutes { get; set; } = 10;
    public double IdleDimPercent { get; set; } = 10;

    /// <summary>Switch presets at set times every day.</summary>
    public bool ScheduleOn { get; set; }
    public List<ScheduledPreset> Schedule { get; set; } = [];

    /// <summary>Lights off while Windows is locked or the PC is asleep.</summary>
    public bool OffWhenLocked { get; set; }

    /// <summary>Lights off every night between these times ("HH:mm"); turning them on ends it until the next night.</summary>
    public bool NightOn { get; set; }
    public string NightFrom { get; set; } = "23:00";
    public string NightTo { get; set; } = "07:00";

    /// <summary>Listen to Counter-Strike 2 and Dota 2 so the Game Events effect can follow them.</summary>
    public bool GameLightingOn { get; set; }
    public int GamePort { get; set; } = GameStateListener.DefaultPort;

    /// <summary>The secret the games send back with each post, written into their config files.</summary>
    public string? GameToken { get; set; }

    /// <summary>Set the monitors' brightness over DDC/CI.</summary>
    public bool MonitorsOn { get; set; }

    /// <summary>The everyday monitor level, 0..100, as set with the slider; null until first read from the monitors.</summary>
    public int? MonitorLevel { get; set; }

    /// <summary>Monitors go down to <see cref="MonitorsOffLevel"/> while the lights are off.</summary>
    public bool MonitorsFollowLights { get; set; }
    public int MonitorsOffLevel { get; set; } = 10;

    /// <summary>Monitors go down to <see cref="MonitorsNightLevel"/> during night hours (the Lights out times).</summary>
    public bool MonitorsNightOn { get; set; }
    public int MonitorsNightLevel { get; set; } = 25;

    /// <summary>Presets that set the monitors to their own level.</summary>
    public bool MonitorPresetLevelsOn { get; set; }
    public List<PresetMonitorLevel> MonitorPresetLevels { get; set; } = [];

    /// <summary>Each monitor's own span for the shared level, by EDID name; monitors not listed use 0..100.</summary>
    public List<MonitorRange> MonitorRanges { get; set; } = [];

    /// <summary>Ctrl+Alt+L turns the lights on or off from anywhere.</summary>
    public bool GlobalHotkeyOn { get; set; }

    /// <summary>Devices page shows tiles with a drawing of each device instead of the detailed list.</summary>
    public bool DevicesGrid { get; set; }

    /// <summary>MyRGB draws the desktop wallpaper (behind the icons) while it runs.</summary>
    public bool WallpaperOn { get; set; }

    public MyRGB.App.Services.Wallpaper.WallpaperSource WallpaperSource { get; set; } = MyRGB.App.Services.Wallpaper.WallpaperSource.Lights;

    /// <summary>The preset the wallpaper plays when its source is <see cref="MyRGB.App.Services.Wallpaper.WallpaperSource.Preset"/>.</summary>
    public string? WallpaperPresetId { get; set; } = "lava-lamp";

    /// <summary>The picture file when the source is a picture.</summary>
    public string? WallpaperPicture { get; set; }

    /// <summary>The video file when the source is a video.</summary>
    public string? WallpaperVideo { get; set; }

    /// <summary>Video wallpapers are decoded by the graphics card (falls back to the processor by itself).</summary>
    public bool VideoUseGpu { get; set; } = true;

    /// <summary>The look, by <see cref="ThemeManager"/> id. Notebook unless picked otherwise.</summary>
    public string Theme { get; set; } = ThemeManager.DefaultId;

    /// <summary>The language MyRGB shows, by code ("de", "pt-BR"); null follows Windows. Applies on the next start.</summary>
    public string? Language { get; set; }
    public OpenRgbOptions OpenRgb { get; set; } = new();
}

public sealed class SettingsStore(string dataDirectory)
{
    public static readonly string DefaultDataDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyRGB");

    public string DataDirectory { get; } = dataDirectory;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path = Path.Combine(dataDirectory, "settings.json");
    private readonly object _gate = new();

    /// <summary>One copy of settings.json per day, the last <see cref="BackupDays"/> days.</summary>
    public string BackupDirectory => Path.Combine(DataDirectory, "backups");

    public const int BackupDays = 7;

    /// <summary>What Load had to do besides reading the file (restored a backup, started fresh); null when nothing. For the log.</summary>
    public string? LoadNote { get; private set; }

    public AppSettings Load()
    {
        LoadNote = null;
        AppSettings settings;
        if (!File.Exists(_path))
            settings = new();
        else if (TryRead(_path, out var read, out var why))
            settings = read;
        else
        {
            // Keep the broken file for inspection, then fall back to the newest backup that reads.
            // Starting empty would lose the user's presets, and the next cloud sync would take that
            // for deleting them everywhere.
            try { File.Copy(_path, _path + ".broken", overwrite: true); } catch (IOException) { }
            var backup = Backups().Select(f => TryRead(f, out var b, out _) ? (File: f, Settings: b) : default)
                .FirstOrDefault(r => r.Settings is not null);
            if (backup.Settings is not null)
            {
                settings = backup.Settings;
                LoadNote = $"settings.json could not be read ({why}); restored {Path.GetFileName(backup.File)}. The unreadable file is settings.json.broken.";
            }
            else
            {
                settings = new();
                LoadNote = $"settings.json could not be read ({why}) and there is no usable backup; starting fresh. The unreadable file is settings.json.broken.";
            }
        }

        // Re-find when the saved path is gone too, e.g. after reinstalling to another folder.
        if (settings.OpenRgb.ExecutablePath is not { } exe || !File.Exists(exe))
            settings.OpenRgb.ExecutablePath = FindOpenRgb();
        return settings;
    }

    private static bool TryRead(string path, out AppSettings settings, out string? why)
    {
        why = null;
        // Another program (a backup tool, an antivirus scan) can hold the file for a moment.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json)!;
                if (settings is not null) return true;
                why = "the file is empty";
                break;
            }
            catch (JsonException ex) { why = ex.Message; break; }
            catch (IOException ex) when (attempt < 4) { why = ex.Message; Thread.Sleep(250); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { why = ex.Message; break; }
        }
        settings = null!;
        return false;
    }

    /// <summary>Backups, newest first.</summary>
    private IEnumerable<string> Backups() =>
        Directory.Exists(BackupDirectory)
            ? Directory.GetFiles(BackupDirectory, "settings-*.json").OrderByDescending(f => f, StringComparer.Ordinal)
            : [];

    public void Save(AppSettings settings) => Save(settings, DateTime.Now);

    public void Save(AppSettings settings, DateTime now)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(DataDirectory);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
            BackUp(now);
            File.Move(tmp, _path, overwrite: true);
        }
    }

    /// <summary>Before the first save of a day, keep the file as it was; drop backups past a week.</summary>
    private void BackUp(DateTime now)
    {
        try
        {
            if (!File.Exists(_path)) return;
            string today = Path.Combine(BackupDirectory, $"settings-{now:yyyy-MM-dd}.json");
            if (File.Exists(today)) return;
            // Only a file that reads back is worth keeping as a backup.
            if (!TryRead(_path, out _, out _)) return;
            Directory.CreateDirectory(BackupDirectory);
            File.Copy(_path, today);
            foreach (var old in Backups().Skip(BackupDays)) File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A missed backup must never stop the save itself.
        }
    }

    /// <summary>The copy the installer bundles next to MyRGB.exe, else a portable OpenRGB under %LOCALAPPDATA%\Programs, or a standard install.</summary>
    private static string? FindOpenRgb()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "OpenRGB", "OpenRGB.exe"),
            Path.Combine(local, "Programs", "OpenRGB", "OpenRGB Windows 64-bit", "OpenRGB.exe"),
            Path.Combine(local, "Programs", "OpenRGB", "OpenRGB.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenRGB", "OpenRGB.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
}
