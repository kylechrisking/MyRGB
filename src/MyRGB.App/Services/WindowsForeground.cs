using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MyRGB.App.Services;

/// <summary>The name of the process that owns the foreground window, for app profiles.</summary>
internal static class WindowsForeground
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>The foreground process's name (no .exe), or null when nothing has focus.</summary>
    public static string? Name()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        try { return Process.GetProcessById((int)pid).ProcessName; }
        catch (Exception) { return null; } // the process could be gone already
    }
}
