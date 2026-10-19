using System.Text.Json;
using MyRGB.Api;
using MyRGB.Core.Devices;
using MyRGB.Core.Layout;
using MyRGB.Core.Presets;
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

    /// <summary>Keep presets in sync across PCs while signed in.</summary>
    public bool SyncPresets { get; set; } = true;

    /// <summary>False until the first-run tour has been finished or skipped.</summary>
    public bool HasSeenWelcome { get; set; }
    public int ApiPort { get; set; } = LocalApiServer.DefaultPort;
    public List<Preset> UserPresets { get; set; } = [];
    public List<DevicePlacement> Layout { get; set; } = [];
    public List<string> ExcludedDevices { get; set; } = [];
    public List<ZoneSize> ZoneSizes { get; set; } = [];
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

    public AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(_path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Json) ?? new()
                : new();
        }
        catch (JsonException)
        {
            // Keep the broken file for inspection rather than silently overwriting it.
            File.Copy(_path, _path + ".broken", overwrite: true);
            settings = new();
        }

        // Re-find when the saved path is gone too, e.g. after reinstalling to another folder.
        if (settings.OpenRgb.ExecutablePath is not { } exe || !File.Exists(exe))
            settings.OpenRgb.ExecutablePath = FindOpenRgb();
        return settings;
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(DataDirectory);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
            File.Move(tmp, _path, overwrite: true);
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
