using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MyRGB.Hid;

namespace MyRGB.Providers.Corsair;

/// <summary>
/// In software mode the Vanguard Pro 96 stops turning its volume wheel into volume keys itself and
/// reports it to the controlling app instead (iCUE does the rest). This does that part: it listens on
/// the keyboard's event interface and presses Windows' own volume keys, so the wheel and the volume
/// pop-up behave exactly as they do without MyRGB.
/// </summary>
/// <remarks>
/// Event reports (interface 3, usage page 0xFF42, usage 2; 65 bytes with the report id), worked out by
/// watching the keyboard while turning and pressing the wheel:
/// <c>00 00 00 00 05 60 00 &lt;i32 steps&gt;</c> for a turn (+1 a click up, -1 down), and
/// <c>00 00 00 00 02 …</c> for key state, where bit 1 of byte 21 is the wheel being pressed.
/// </remarks>
internal sealed class VolumeWheel : IAsyncDisposable
{
    public const string InterfacePath = "vid_1b1c&pid_2b0e&mi_03";
    private const ushort EventUsagePage = 0xFF42, EventUsage = 0x02;

    private readonly FileStream _stream;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _reader;
    private bool _pressed;

    private VolumeWheel(FileStream stream, ILogger log)
    {
        _stream = stream;
        _log = log;
        _reader = Task.Run(ReadLoopAsync);
    }

    /// <summary>Null when the event interface isn't there or can't be opened; the lighting still works.</summary>
    public static VolumeWheel? TryOpen(ILogger log)
    {
        try
        {
            var iface = HidDevices.Find(InterfacePath).FirstOrDefault(i => i.UsagePage == EventUsagePage && i.Usage == EventUsage);
            return iface is null ? null : new VolumeWheel(HidDevices.Open(iface.Path), log);
        }
        catch (IOException ex)
        {
            log.LogWarning(ex, "Couldn't open the keyboard's volume wheel");
            return null;
        }
    }

    /// <summary>What one event report asks for: volume steps (+ up, - down) and whether to toggle mute.</summary>
    internal static (int Steps, bool Mute) Parse(ReadOnlySpan<byte> report, ref bool pressed)
    {
        if (report.Length < 22 || report[1] != 0 || report[2] != 0 || report[3] != 0) return (0, false);
        if (report[4] == 0x05 && report[5] == 0x60)
            return (Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(report[7..]), -20, 20), false);
        if (report[4] == 0x02)
        {
            bool down = (report[21] & 0x02) != 0;
            bool mute = down && !pressed; // on press, not on release
            pressed = down;
            return (0, mute);
        }
        return (0, false);
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
                var (steps, mute) = Parse(buf.AsSpan(0, n), ref _pressed);
                for (int i = 0; i < Math.Abs(steps); i++) Tap(steps > 0 ? VolumeUp : VolumeDown);
                if (mute) Tap(VolumeMute);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            if (!_cts.IsCancellationRequested) _log.LogDebug(ex, "Volume wheel stopped");
        }
    }

    private const byte VolumeMute = 0xAD, VolumeDown = 0xAE, VolumeUp = 0xAF; // VK_VOLUME_*
    private const uint KeyUp = 0x0002, ExtendedKey = 0x0001;

    private static void Tap(byte key)
    {
        keybd_event(key, 0, ExtendedKey, 0);
        keybd_event(key, 0, ExtendedKey | KeyUp, 0);
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
