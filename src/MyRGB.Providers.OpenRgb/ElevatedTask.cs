using System.Text.RegularExpressions;

namespace MyRGB.Providers.OpenRgb;

/// <summary>
/// Checks the elevated OpenRGB task's launcher (Program Files\MyRGB\openrgb-hidden.vbs, written by
/// tools\setup-openrgb-admin.ps1). The task runs OpenRGB as administrator, so OpenRGB must live where
/// only administrators can change it; one in the user's own folder hands admin rights to anything
/// that swaps the file. Turning on the sensor helper from 0.4.0 to 0.7.6 re-pointed it there.
/// </summary>
public static partial class ElevatedTask
{
    /// <summary>The 64-bit Program Files, as the setup script uses, even from a 32-bit process.</summary>
    public static string ProgramFiles =>
        Environment.GetEnvironmentVariable("ProgramW6432") is { Length: > 0 } pf ? pf : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    public static string LauncherPath => Path.Combine(ProgramFiles, "MyRGB", "openrgb-hidden.vbs");

    /// <summary>Where the admin copy of OpenRGB belongs.</summary>
    public static string SafeOpenRgbDir => Path.Combine(ProgramFiles, "MyRGB", "OpenRGB");

    /// <summary>The exe a launcher script starts, or null if it can't be read.</summary>
    public static string? ExeFrom(string launcher) => RunLine().Match(launcher) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>Whether <paramref name="exe"/> sits under <paramref name="programFiles"/>, which only administrators can change.</summary>
    public static bool IsAdminOnly(string exe, string programFiles)
    {
        string full = Path.GetFullPath(exe);
        string root = Path.GetFullPath(programFiles).TrimEnd('\\') + '\\';
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The user-writable OpenRGB the task would run as administrator, or null when there's no task
    /// launcher or it's safe.
    /// </summary>
    public static string? UnsafeExe()
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !File.Exists(LauncherPath)) return null;
            return ExeFrom(File.ReadAllText(LauncherPath)) is { } exe && !IsAdminOnly(exe, ProgramFiles) ? exe : null;
        }
        catch (Exception)
        {
            return null; // can't tell; say nothing rather than cry wolf
        }
    }

    [GeneratedRegex(@"sh\.Run """"""(.+?)""""")]
    private static partial Regex RunLine();
}
