using System.Diagnostics;

namespace MyRGB.App.Services.Wallpaper;

/// <summary>
/// Gives the desktop back to Windows if MyRGB stops without closing its wallpaper, for example when it
/// is ended from Task Manager or crashes. Explorer doesn't redraw the spot our windows covered, so the
/// last frame stays on the desktop until something tells it to.
///
/// Two things cover that. While the wallpaper shows, a small file says so; a clean close removes it.
/// A second copy of MyRGB.exe, started with <see cref="WatchArgument"/> the first time the wallpaper
/// shows, waits in the background for this one to end, and repaints the desktop if that file is still
/// there. And if even that missed it, the next start repaints once when it finds the file.
/// </summary>
internal static class DesktopGuard
{
    public const string WatchArgument = "--restore-desktop-after";

    private static readonly string Marker = Path.Combine(SettingsStore.DefaultDataDirectory, "wallpaper-showing");
    private static bool _watching;

    /// <summary>At start: if the last run ended with its wallpaper still up, have Windows redraw its own.</summary>
    public static void CleanUpAfterLastRun()
    {
        if (OwnerInMarker() is null) return;
        DesktopLayer.RepaintWallpaper();
        Clear();
    }

    /// <summary>The wallpaper's windows are on the desktop now.</summary>
    public static void Showing()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Marker)!);
            File.WriteAllText(Marker, Environment.ProcessId.ToString());
        }
        catch (Exception) { return; } // without the file the watcher would do nothing anyway
        StartWatcher();
    }

    /// <summary>The wallpaper's windows are gone and Windows has its desktop back.</summary>
    public static void Clear()
    {
        try { File.Delete(Marker); } catch (Exception) { /* the next start repaints once, which is harmless */ }
    }

    /// <summary>
    /// Runs in the watching copy of MyRGB.exe: wait for <paramref name="owner"/> to end, then repaint the
    /// desktop if it ended with the wallpaper up. Opens no window and does nothing else.
    /// </summary>
    public static void Watch(int owner)
    {
        try
        {
            using var process = Process.GetProcessById(owner);
            process.WaitForExit();
        }
        catch (ArgumentException) { /* already gone */ }
        // A clean close removed the file, or a newer MyRGB (a handover to "Run as administrator") owns it now.
        if (OwnerInMarker() != owner) return;
        // GUESS: give Windows a moment to finish tearing down the dead process's windows before the repaint.
        Thread.Sleep(500);
        DesktopLayer.RepaintWallpaper();
        Clear();
    }

    private static void StartWatcher()
    {
        if (_watching || Environment.ProcessPath is not { } exe) return;
        try
        {
            // The same exe, so no new program is involved; it is a windowed app, so no console shows.
            Process.Start(new ProcessStartInfo(exe, $"{WatchArgument} {Environment.ProcessId}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            })?.Dispose();
            _watching = true;
        }
        catch (Exception) { /* the next start still repaints */ }
    }

    private static int? OwnerInMarker()
    {
        try { return File.Exists(Marker) && int.TryParse(File.ReadAllText(Marker).Trim(), out int id) ? id : null; }
        catch (Exception) { return null; }
    }
}
