using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MyRGB.Core.Devices;
using MyRGB.Hid;

namespace MyRGB.Providers.Corsair;

/// <summary>
/// In software mode the Vanguard Pro 96 stops acting on its wheel and some Fn shortcuts itself and
/// reports them to the controlling app instead (iCUE does the rest). This does that part: the wheel
/// and the media-style keys press Windows' own keys, and the lighting keys go to MyRGB.
/// </summary>
/// <remarks>
/// Event reports (interface 3, usage page 0xFF42, usage 2; 65 bytes with the report id), worked out by
/// watching the keyboard while using each control:
/// <list type="bullet">
/// <item><c>00 00 00 00 05 60 00 &lt;i32 steps&gt;</c>: a wheel turn, +1 a click up, -1 down.</item>
/// <item><c>00 00 00 00 02 …</c>: which keys are down, one bit each. Byte 20 bit 2 is Fn; byte 21 bit 1
/// is the wheel pressed; F1–F6 are byte 12 bits 2–7 and F7–F12 byte 13 bits 0–5.</item>
/// </list>
/// The Fn shortcuts the keyboard still handles itself (media and volume, F5–F11) reach Windows as
/// normal media keys, so only F1–F4 and F12 are acted on here.
/// </remarks>
internal sealed class KeyboardControls : IAsyncDisposable
{
    public const string InterfacePath = "vid_1b1c&pid_2b0e&mi_03";
    private const ushort EventUsagePage = 0xFF42, EventUsage = 0x02;

    /// <summary>What the controls ask for, in the order the keyboard reports them.</summary>
    internal enum Control { None, VolumeSteps, Mute, ScrollLock, NextProfile, BrightnessDown, BrightnessUp, DialMode }

    private readonly FileStream _stream;
    private readonly ILogger _log;
    private readonly Action<DeviceCommand> _command;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _reader;
    private readonly KeyState _keys = new();
    private bool _dialBrightness;

    private KeyboardControls(FileStream stream, ILogger log, Action<DeviceCommand> command)
    {
        _stream = stream;
        _log = log;
        _command = command;
        _reader = Task.Run(ReadLoopAsync);
    }

    /// <summary>Null when the event interface isn't there or can't be opened; the lighting still works.</summary>
    public static KeyboardControls? TryOpen(ILogger log, Action<DeviceCommand> command)
    {
        try
        {
            var iface = HidDevices.Find(InterfacePath).FirstOrDefault(i => i.UsagePage == EventUsagePage && i.Usage == EventUsage);
            return iface is null ? null : new KeyboardControls(HidDevices.Open(iface.Path), log, command);
        }
        catch (IOException ex)
        {
            log.LogWarning(ex, "Couldn't open the keyboard's controls");
            return null;
        }
    }

    /// <summary>Keys held in the last report, so each press acts once and holding doesn't repeat.</summary>
    internal sealed class KeyState
    {
        public bool Wheel, F1, F2, F3, F4, F12;
    }

    /// <summary>What one event report asks for. Wheel turns come back as steps (+ up, - down).</summary>
    internal static List<(Control Control, int Steps)> Parse(ReadOnlySpan<byte> report, KeyState keys)
    {
        var actions = new List<(Control, int)>();
        if (report.Length < 22 || report[1] != 0 || report[2] != 0 || report[3] != 0) return actions;

        if (report[4] == 0x05 && report[5] == 0x60)
        {
            int steps = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(report[7..]), -20, 20);
            if (steps != 0) actions.Add((Control.VolumeSteps, steps));
            return actions;
        }
        if (report[4] != 0x02) return actions;

        bool fn = (report[20] & 0x04) != 0;
        // Edge-triggered: act when a key goes down, not while it's held or when it comes up.
        void Edge(ref bool was, bool now, Control control)
        {
            if (now && !was) actions.Add((control, 0));
            was = now;
        }
        Edge(ref keys.Wheel, (report[21] & 0x02) != 0, Control.Mute);
        Edge(ref keys.F1, fn && (report[12] & 0x04) != 0, Control.ScrollLock);
        Edge(ref keys.F2, fn && (report[12] & 0x08) != 0, Control.NextProfile);
        Edge(ref keys.F3, fn && (report[12] & 0x10) != 0, Control.BrightnessDown);
        Edge(ref keys.F4, fn && (report[12] & 0x20) != 0, Control.BrightnessUp);
        Edge(ref keys.F12, fn && (report[13] & 0x20) != 0, Control.DialMode);
        return actions;
    }

    private async Task ReadLoopAsync()
    {
        var buf = new byte[65];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int n = await _stream.ReadAsync(buf, _cts.Token);
                if (n <= 0) break;
                foreach (var (control, steps) in Parse(buf.AsSpan(0, n), _keys)) Perform(control, steps);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            if (!_cts.IsCancellationRequested) _log.LogDebug(ex, "Keyboard controls stopped");
        }
    }

    private void Perform(Control control, int steps)
    {
        switch (control)
        {
            // The wheel turns the volume, or MyRGB's brightness after Fn+F12 (press the wheel: mute either way).
            case Control.VolumeSteps when _dialBrightness:
                for (int i = 0; i < Math.Abs(steps); i++) _command(steps > 0 ? DeviceCommand.BrightnessNudgeUp : DeviceCommand.BrightnessNudgeDown);
                break;
            case Control.VolumeSteps:
                for (int i = 0; i < Math.Abs(steps); i++) Tap(steps > 0 ? VolumeUp : VolumeDown);
                break;
            case Control.Mute: Tap(VolumeMute); break;
            case Control.ScrollLock: Tap(ScrollLock); break;
            case Control.NextProfile: _command(DeviceCommand.NextPreset); break;
            case Control.BrightnessDown: _command(DeviceCommand.BrightnessDown); break;
            case Control.BrightnessUp: _command(DeviceCommand.BrightnessUp); break;
            case Control.DialMode:
                _dialBrightness = !_dialBrightness;
                _command(_dialBrightness ? DeviceCommand.DialControlsBrightness : DeviceCommand.DialControlsVolume);
                break;
        }
    }

    private const byte ScrollLock = 0x91, VolumeMute = 0xAD, VolumeDown = 0xAE, VolumeUp = 0xAF; // VK_*
    private const uint KeyUp = 0x0002, ExtendedKey = 0x0001;

    private static void Tap(byte key)
    {
        uint ext = key == ScrollLock ? 0 : ExtendedKey;
        keybd_event(key, 0, ext, 0);
        keybd_event(key, 0, ext | KeyUp, 0);
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, nuint extraInfo);

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await _stream.DisposeAsync();
        try { await _reader; } catch { /* closing */ }
        _cts.Dispose();
    }
}
