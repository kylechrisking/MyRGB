using System.Diagnostics;
using Microsoft.Win32;
using MyRGB.Core.Vendors;

namespace MyRGB.App.Services;

/// <summary>Reads Windows' Installed apps list (the Uninstall registry keys) and launches uninstallers.</summary>
public static class InstalledPrograms
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public static IReadOnlyList<InstalledProgram> Read()
    {
        var result = new List<InstalledProgram>();
        if (!OperatingSystem.IsWindows()) return result;

        foreach (var (hive, view) in new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Default),
        })
        {
            // Any program the user runs can write per-user uninstall entries. Running as administrator,
            // MyRGB would start those commands with admin rights and no prompt, so it skips them then.
            if (hive == RegistryHive.CurrentUser && Program.IsElevated) continue;
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(UninstallKey);
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(name);
                    if (key?.GetValue("DisplayName") is not string display || string.IsNullOrWhiteSpace(display)) continue;
                    if (key.GetValue("SystemComponent") is int sc && sc == 1) continue; // hidden from Installed apps too
                    result.Add(new InstalledProgram(
                        display.Trim(),
                        key.GetValue("Publisher") as string,
                        key.GetValue("DisplayVersion") as string,
                        key.GetValue("UninstallString") as string));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        return result;
    }

    public static IEnumerable<string> RunningProcessNames()
    {
        foreach (var p in Process.GetProcesses())
        {
            string? name = null;
            try { name = p.ProcessName; } catch (InvalidOperationException) { }
            finally { p.Dispose(); }
            if (name is not null) yield return name;
        }
    }

    /// <summary>
    /// Start the program's own uninstaller. It shows its own window and usually a UAC prompt; that
    /// is the user's choice, made with the Uninstall button.
    /// </summary>
    public static bool LaunchUninstaller(InstalledProgram program)
    {
        if (VendorAppCatalog.UninstallCommand(program) is not { } cmd) return false;
        var (exe, args) = Split(cmd);
        Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true })?.Dispose();
        return true;
    }

    /// <summary>Split a registry command line into the executable and its arguments.</summary>
    internal static (string Exe, string Args) Split(string cmd)
    {
        cmd = cmd.Trim();
        if (cmd.StartsWith('"'))
        {
            int end = cmd.IndexOf('"', 1);
            return end > 0 ? (cmd[1..end], cmd[(end + 1)..].Trim()) : (cmd.Trim('"'), "");
        }
        // Unquoted: the executable ends at ".exe" (paths with spaces are often left unquoted).
        int exe = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return (cmd[..(exe + 4)], cmd[(exe + 4)..].Trim());
        int space = cmd.IndexOf(' ');
        return space > 0 ? (cmd[..space], cmd[(space + 1)..]) : (cmd, "");
    }
}
