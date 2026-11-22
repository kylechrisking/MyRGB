using Microsoft.Win32;

namespace MyRGB.App.Services;

/// <summary>"Start with Windows" via the per-user Run key (no admin needed). Switched only by the toggle, re-pointed by the installed copy.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MyRGB";

    public static bool IsEnabled
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return false;
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    /// <summary>
    /// When "Start with Windows" is on but points at another copy of MyRGB (a dev build, an old install
    /// folder), the installed copy takes the entry back, so Windows starts the version that updates.
    /// On 2026-11-22 the entry still pointed at the repo's Debug build, not the installed copy.
    /// </summary>
    public static void ClaimForInstalledCopy()
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { } self) return;
        string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "MyRGB");
        if (!self.StartsWith(installed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is not string registered) return;
        if (!registered.Contains(self, StringComparison.OrdinalIgnoreCase)) key.SetValue(ValueName, $"\"{self}\" --minimized");
    }

    public static void Set(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --minimized");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
