using System.Runtime.InteropServices;

namespace MyRGB.App.Services.Wallpaper;

/// <summary>
/// Puts a window behind the desktop icons, where the wallpaper is drawn. Explorer keeps that layer in
/// a "WorkerW" window it only creates when asked (message 0x052C to Progman, the trick every animated
/// wallpaper app uses). Up to Windows 11 23H2 the WorkerW is a top-level window behind the icons and
/// ours becomes its child; from 24H2 it is a child of Progman itself, and ours goes into Progman
/// between the icons (SHELLDLL_DefView) and that WorkerW.
/// </summary>
internal static class DesktopLayer
{
    /// <summary>The window ours was put into, or null if Explorer's desktop couldn't be found.</summary>
    public static IntPtr? Attach(IntPtr window, int x, int y, int width, int height)
    {
        var progman = FindWindowW("Progman", null);
        if (progman == IntPtr.Zero) return null;
        // Ask Explorer to split the wallpaper into its own WorkerW. Both forms are sent: builds differ in which they answer.
        SendMessageTimeoutW(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, SmtoNormal, 1000, out _);
        SendMessageTimeoutW(progman, 0x052C, (IntPtr)0xD, (IntPtr)0x1, SmtoNormal, 1000, out _);

        // Coordinates inside the parent, which spans the whole virtual screen.
        int left = x - GetSystemMetrics(SmXVirtualScreen), top = y - GetSystemMetrics(SmYVirtualScreen);

        var innerWorker = FindWindowExW(progman, IntPtr.Zero, "WorkerW", null);
        if (innerWorker != IntPtr.Zero)
        {
            // 24H2 and later.
            var icons = FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            MakeChild(window, progman);
            // "Insert after" the icons puts ours just below them, so it covers the WorkerW but not the icons.
            SetWindowPos(window, icons != IntPtr.Zero ? icons : IntPtr.Zero, left, top, width, height, SwpNoActivate | SwpShowWindow);
            return progman;
        }

        // Older Windows: the top-level WorkerW that comes after the one holding the icons.
        IntPtr worker = IntPtr.Zero;
        EnumWindows((top, _) =>
        {
            if (FindWindowExW(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                worker = FindWindowExW(IntPtr.Zero, top, "WorkerW", null);
            return worker == IntPtr.Zero;
        }, IntPtr.Zero);
        if (worker == IntPtr.Zero) return null;
        MakeChild(window, worker);
        SetWindowPos(window, IntPtr.Zero, left, top, width, height, SwpNoActivate | SwpShowWindow | SwpNoZOrder);
        return worker;
    }

    /// <summary>Whether <paramref name="window"/> is still inside <paramref name="parent"/>; false once Explorer restarted.</summary>
    public static bool StillAttached(IntPtr window, IntPtr parent) => IsWindow(parent) && GetParent(window) == parent;

    /// <summary>Take the window out again and have Windows repaint its own wallpaper over where it was.</summary>
    public static void Detach(IntPtr window)
    {
        if (IsWindow(window))
        {
            ShowWindow(window, 0);
            SetParent(window, IntPtr.Zero);
        }
        RepaintWallpaper();
    }

    /// <summary>Setting the current wallpaper again makes Explorer redraw it (the layer can stay black otherwise).</summary>
    public static void RepaintWallpaper()
    {
        var path = CurrentWallpaperPath();
        SystemParametersInfoW(SpiSetDeskWallpaper, 0, path ?? "", 0);
    }

    /// <summary>The picture Windows shows as wallpaper, or null for a plain color or slideshow it can't name.</summary>
    public static string? CurrentWallpaperPath()
    {
        var buffer = new char[1024];
        if (!SystemParametersInfoW(SpiGetDeskWallpaper, (uint)buffer.Length, buffer, 0)) return null;
        var path = new string(buffer, 0, Array.IndexOf(buffer, '\0') is var end and >= 0 ? end : buffer.Length);
        return path.Length > 0 && File.Exists(path) ? path : null;
    }

    /// <summary>True when the foreground window fills the given screen rectangle (a game or a full-screen video).</summary>
    public static bool CoveredByFullScreen(int x, int y, int width, int height)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == GetShellWindow() || !GetWindowRect(fg, out var r)) return false;
        var cls = new char[64];
        int n = GetClassNameW(fg, cls, cls.Length);
        var name = new string(cls, 0, Math.Max(0, n));
        if (name is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        return r.Left <= x && r.Top <= y && r.Right >= x + width && r.Bottom >= y + height;
    }

    /// <summary>The plain background color Windows shows when there is no wallpaper picture.</summary>
    public static MyRGB.Core.Rgb DesktopColor()
    {
        uint c = GetSysColor(1); // COLOR_DESKTOP, as 0x00BBGGRR
        return new MyRGB.Core.Rgb((byte)c, (byte)(c >> 8), (byte)(c >> 16));
    }

    private static void MakeChild(IntPtr window, IntPtr parent)
    {
        long style = GetWindowLongPtrW(window, GwlStyle);
        style = (style & ~(WsPopup | WsCaption | WsThickFrame)) | WsChild;
        SetWindowLongPtrW(window, GwlStyle, (IntPtr)style);
        SetParent(window, parent);
    }

    private const int SmXVirtualScreen = 76, SmYVirtualScreen = 77;
    private const uint SmtoNormal = 0;
    private const uint SwpNoActivate = 0x0010, SwpShowWindow = 0x0040, SwpNoZOrder = 0x0004;
    private const int GwlStyle = -16;
    private const long WsChild = 0x40000000, WsPopup = 0x80000000, WsCaption = 0x00C00000, WsThickFrame = 0x00040000;
    private const uint SpiGetDeskWallpaper = 0x0073, SpiSetDeskWallpaper = 0x0014;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr childAfter, string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern long GetWindowLongPtrW(IntPtr hwnd, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint param, string value, uint winIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint action, uint param, [Out] char[] value, uint winIni);

    [DllImport("user32.dll")]
    private static extern uint GetSysColor(int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, [Out] char[] name, int max);
}
