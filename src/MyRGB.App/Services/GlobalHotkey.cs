using System.Runtime.InteropServices;

namespace MyRGB.App.Services;

/// <summary>
/// Ctrl+Alt+L toggles the lights from anywhere, without installing a vendor app's whole hotkey
/// engine. Runs its own message-only window on a dedicated thread, since RegisterHotKey needs a
/// real Win32 message loop and the app may start with no visible window at all.
/// </summary>
internal sealed class GlobalHotkey : IDisposable
{
    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_APP_SET_ENABLED = 0x8001;
    private const uint MOD_CONTROL = 0x0002, MOD_ALT = 0x0001, MOD_NOREPEAT = 0x4000;
    private const uint VK_L = 0x4C;
    private const int HotkeyId = 1;
    private const int HWND_MESSAGE = -3;

    public event Action? Pressed;

    private readonly WndProcDelegate _wndProc;
    private readonly ManualResetEventSlim _ready = new();
    private IntPtr _hwnd;
    private bool _registered;

    public GlobalHotkey()
    {
        _wndProc = WndProc;
        if (!OperatingSystem.IsWindows()) return;
        new Thread(Run) { IsBackground = true, Name = "GlobalHotkey" }.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    /// <summary>Thread-safe; the actual (un)registration happens on the window's own thread.</summary>
    public void SetEnabled(bool on)
    {
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_APP_SET_ENABLED, on ? 1 : 0, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_DESTROY, 0, IntPtr.Zero);
    }

    private void Run()
    {
        var className = $"MyRGBHotkey-{Environment.ProcessId}";
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = GetModuleHandle(null),
            lpszClassName = className,
        };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(0, className, "", 0, 0, 0, 0, 0, new IntPtr(HWND_MESSAGE), IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        _ready.Set();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_HOTKEY when wParam.ToInt32() == HotkeyId:
                Pressed?.Invoke();
                return IntPtr.Zero;
            case WM_APP_SET_ENABLED:
                bool on = wParam != IntPtr.Zero;
                if (on && !_registered) _registered = RegisterHotKey(hWnd, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_L);
                else if (!on && _registered) { UnregisterHotKey(hWnd, HotkeyId); _registered = false; }
                return IntPtr.Zero;
            case WM_DESTROY:
                if (_registered) UnregisterHotKey(hWnd, HotkeyId);
                DestroyWindow(hWnd);
                PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }

    private const uint WM_DESTROY = 0x0002;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x, y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, int wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? name);
}
