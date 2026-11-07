using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace MyRGB.App.Services;

/// <summary>
/// Global keyboard shortcuts that work from any app, without installing a vendor app's whole
/// hotkey engine: Ctrl+Alt+L toggles the lights, Ctrl+Alt+N jumps to the next preset. Runs one
/// message-only window on a dedicated thread, since RegisterHotKey needs a real Win32 message
/// loop and the app may start with no visible window at all.
/// </summary>
internal sealed class GlobalHotkeys : IDisposable
{
    public const int ToggleLightsId = 1;
    public const int NextPresetId = 2;

    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_APP_SYNC = 0x8001;
    private const uint MOD_CONTROL = 0x0002, MOD_ALT = 0x0001, MOD_NOREPEAT = 0x4000;
    private const uint VK_L = 0x4C, VK_N = 0x4E;
    private const int HWND_MESSAGE = -3;

    public event Action<int>? Pressed;

    private readonly WndProcDelegate _wndProc;
    private readonly ManualResetEventSlim _ready = new();
    private readonly ConcurrentDictionary<int, (uint Modifiers, uint VirtualKey, bool On)> _desired = new();
    private readonly HashSet<int> _registered = [];
    private IntPtr _hwnd;

    public GlobalHotkeys()
    {
        _wndProc = WndProc;
        if (!OperatingSystem.IsWindows()) return;
        new Thread(Run) { IsBackground = true, Name = "GlobalHotkeys" }.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    public void SetToggleLights(bool on) => Set(ToggleLightsId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_L, on);
    public void SetNextPreset(bool on) => Set(NextPresetId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_N, on);

    /// <summary>Thread-safe; the actual (un)registration happens on the window's own thread.</summary>
    private void Set(int id, uint modifiers, uint virtualKey, bool on)
    {
        _desired[id] = (modifiers, virtualKey, on);
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_APP_SYNC, 0, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_DESTROY, 0, IntPtr.Zero);
    }

    private void Run()
    {
        var className = $"MyRGBHotkeys-{Environment.ProcessId}";
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
            case WM_HOTKEY:
                Pressed?.Invoke(wParam.ToInt32());
                return IntPtr.Zero;
            case WM_APP_SYNC:
                foreach (var (id, (modifiers, virtualKey, on)) in _desired)
                {
                    bool isRegistered = _registered.Contains(id);
                    if (on && !isRegistered) { if (RegisterHotKey(hWnd, id, modifiers, virtualKey)) _registered.Add(id); }
                    else if (!on && isRegistered) { UnregisterHotKey(hWnd, id); _registered.Remove(id); }
                }
                return IntPtr.Zero;
            case WM_DESTROY:
                foreach (var id in _registered) UnregisterHotKey(hWnd, id);
                _registered.Clear();
                DestroyWindow(hWnd);
                PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }

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
