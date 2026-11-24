using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace MyRGB.App.Services;

/// <summary>One global shortcut as Settings shows it; <see cref="Taken"/> when another app already holds the keys.</summary>
public sealed record HotkeyStatus(string Keys, string Does, bool Taken);

/// <summary>
/// Global keyboard shortcuts that work from any app, without installing a vendor app's whole
/// hotkey engine: lights on/off, next and previous preset, lights and screen brightness. Runs one
/// message-only window on a dedicated thread, since RegisterHotKey needs a real Win32 message
/// loop and the app may start with no visible window at all.
/// </summary>
internal sealed class GlobalHotkeys : IDisposable
{
    public const int ToggleLightsId = 1, NextPresetId = 2, PreviousPresetId = 3,
        BrighterId = 4, DimmerId = 5, ScreensBrighterId = 6, ScreensDimmerId = 7;

    private const uint WM_HOTKEY = 0x0312;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_APP_SYNC = 0x8001;
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_NOREPEAT = 0x4000;
    private const uint CtrlAlt = MOD_CONTROL | MOD_ALT, CtrlAltShift = CtrlAlt | MOD_SHIFT;
    private const uint VK_B = 0x42, VK_L = 0x4C, VK_N = 0x4E, VK_PLUS = 0xBB, VK_MINUS = 0xBD;
    private const int HWND_MESSAGE = -3;

    private sealed record Key(int Id, uint Modifiers, uint VirtualKey, bool Repeats, string Keys, string Does);

    /// <summary>Every shortcut, in the order Settings lists them. Brightness keys repeat while held.</summary>
    private static readonly Key[] All =
    [
        new(ToggleLightsId, CtrlAlt, VK_L, false, "Ctrl+Alt+L", "lights on or off"),
        new(NextPresetId, CtrlAlt, VK_N, false, "Ctrl+Alt+N", "next preset"),
        new(PreviousPresetId, CtrlAlt, VK_B, false, "Ctrl+Alt+B", "back to the previous preset"),
        new(BrighterId, CtrlAlt, VK_PLUS, true, "Ctrl+Alt+=", "lights brighter"),
        new(DimmerId, CtrlAlt, VK_MINUS, true, "Ctrl+Alt+-", "lights dimmer"),
        new(ScreensBrighterId, CtrlAltShift, VK_PLUS, true, "Ctrl+Alt+Shift+=", "screens brighter (with Monitors on)"),
        new(ScreensDimmerId, CtrlAltShift, VK_MINUS, true, "Ctrl+Alt+Shift+-", "screens dimmer (with Monitors on)"),
    ];

    public event Action<int>? Pressed;

    /// <summary>Which keys registered changed. On the hotkey thread.</summary>
    public event Action? StatusChanged;

    private readonly WndProcDelegate _wndProc;
    private readonly ManualResetEventSlim _ready = new();
    private readonly HashSet<int> _registered = [];
    private readonly ConcurrentDictionary<int, bool> _taken = new();
    private volatile bool _on;
    private IntPtr _hwnd;

    public GlobalHotkeys()
    {
        _wndProc = WndProc;
        if (!OperatingSystem.IsWindows()) return;
        new Thread(Run) { IsBackground = true, Name = "GlobalHotkeys" }.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    /// <summary>Thread-safe; the actual (un)registration happens on the window's own thread.</summary>
    public void SetAll(bool on)
    {
        _on = on;
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_APP_SYNC, 0, IntPtr.Zero);
    }

    /// <summary>Every shortcut with whether another app holds it (only known while they're switched on).</summary>
    public IReadOnlyList<HotkeyStatus> Status() =>
        All.Select(k => new HotkeyStatus(k.Keys, k.Does, _taken.TryGetValue(k.Id, out bool t) && t)).ToList();

    /// <summary>The list without a running hotkey thread, e.g. when hotkeys were left out at startup.</summary>
    public static IReadOnlyList<HotkeyStatus> Plain() => All.Select(k => new HotkeyStatus(k.Keys, k.Does, false)).ToList();

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
                bool on = _on;
                foreach (var k in All)
                {
                    bool isRegistered = _registered.Contains(k.Id);
                    if (on && !isRegistered)
                    {
                        uint mods = k.Modifiers | (k.Repeats ? 0 : MOD_NOREPEAT);
                        bool ok = RegisterHotKey(hWnd, k.Id, mods, k.VirtualKey);
                        if (ok) _registered.Add(k.Id);
                        _taken[k.Id] = !ok; // the only reason it fails for a fixed key is that another app has it
                    }
                    else if (!on)
                    {
                        if (isRegistered) { UnregisterHotKey(hWnd, k.Id); _registered.Remove(k.Id); }
                        _taken[k.Id] = false;
                    }
                }
                StatusChanged?.Invoke();
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
