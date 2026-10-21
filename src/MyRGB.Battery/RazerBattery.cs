using Microsoft.Win32.SafeHandles;
using MyRGB.Core.Devices;
using MyRGB.Hid;

namespace MyRGB.Battery;

/// <summary>
/// Razer wireless mice and keyboards. Razer devices take 90-byte commands as HID feature reports
/// (the protocol Synapse and OpenRazer use); "get battery level" is class 0x07, command 0x80.
/// </summary>
public sealed class RazerBatteryProvider : IBatteryProvider
{
    // PID → (name, transaction id). The transaction id is a per-model constant Razer devices expect.
    private static readonly Dictionary<ushort, (string Name, byte TransactionId)> Known = new()
    {
        [0x00AA] = ("Razer Basilisk V3 Pro", 0x1F),
        [0x00AB] = ("Razer Basilisk V3 Pro", 0x1F),
        [0x00B9] = ("Razer Basilisk V3 X HyperSpeed", 0x1F),
        [0x00CC] = ("Razer Basilisk V3 Pro 35K", 0x1F),
        [0x00CD] = ("Razer Basilisk V3 Pro 35K", 0x1F),
        [0x007C] = ("Razer DeathAdder V2 Pro", 0x3F),
        [0x007D] = ("Razer DeathAdder V2 Pro", 0x3F),
        [0x008F] = ("Razer Naga Pro", 0x1F),
        [0x0090] = ("Razer Naga Pro", 0x1F),
        [0x00A5] = ("Razer Viper V2 Pro", 0x1F),
        [0x00A6] = ("Razer Viper V2 Pro", 0x1F),
        [0x00B6] = ("Razer DeathAdder V3 Pro", 0x1F),
        [0x00B7] = ("Razer DeathAdder V3 Pro", 0x1F),
    };

    public IReadOnlyList<IBatterySource> Discover()
    {
        var sources = new List<IBatterySource>();
        foreach (var (pid, (name, tid)) in Known)
        {
            // Razer feature reports are 90 bytes + the report id.
            var iface = HidDevices.Find($"vid_1532&pid_{pid:x4}").FirstOrDefault(i => i.FeatureReportLength == RazerReport.Length + 1);
            if (iface is not null) sources.Add(new RazerBatterySource(iface.Path, name, $"usb:1532:{pid:x4}", tid));
        }
        return sources;
    }
}

internal sealed class RazerBatterySource(string path, string name, string hardwareKey, byte transactionId) : IBatterySource
{
    public string HardwareKey => hardwareKey;
    public string Name => name;

    public Task<BatteryStatus?> ReadAsync(CancellationToken ct) => Task.Run(() =>
    {
        using var h = HidDevices.OpenForFeatures(path);
        var level = Query(h, 0x80);
        if (level is null) return null; // asleep or out of range: the dongle answers "no response"
        var charging = Query(h, 0x84);
        return (BatteryStatus?)new BatteryStatus((int)Math.Round(level.Value / 255.0 * 100), charging == 1);
    }, ct);

    /// <summary>Send a class 0x07 (power) query and return arguments[1] of the reply.</summary>
    private byte? Query(SafeFileHandle h, byte command)
    {
        var request = RazerReport.Build(transactionId, commandClass: 0x07, command, dataSize: 0x02);
        // The device keeps one reply slot, and OpenRGB rewrites it with every lighting frame it
        // sends, so read it back quickly and often, and only accept our own command echoed back.
        // A dongle whose mouse is asleep answers with a failure; one of those can also be a
        // hiccup while the radio is busy, so it takes a few in a row to call the mouse asleep.
        int noResponse = 0;
        for (int attempt = 0; attempt < 10 && noResponse < 3; attempt++)
        {
            if (!HidDevices.SetFeature(h, request)) return null;
            for (int poll = 0; poll < 10; poll++)
            {
                Thread.Sleep(4);
                var reply = new byte[RazerReport.Length + 1];
                if (!HidDevices.GetFeature(h, reply)) continue;
                var r = RazerReport.Parse(reply, 0x07, command);
                if (r.Ok) return r.Argument1;
                if (r.NoResponse) { noResponse++; break; }
            }
        }
        return null;
    }
}

/// <summary>The 90-byte Razer report layout (plus a leading report id 0).</summary>
internal static class RazerReport
{
    public const int Length = 90;

    // Status byte values in replies.
    private const byte Busy = 0x01, Success = 0x02, Failure = 0x03, Timeout = 0x04, NotSupported = 0x05;

    public static byte[] Build(byte transactionId, byte commandClass, byte commandId, byte dataSize)
    {
        var buf = new byte[Length + 1]; // [0] = report id
        var r = buf.AsSpan(1);
        r[1] = transactionId;
        r[5] = dataSize;
        r[6] = commandClass;
        r[7] = commandId;
        r[88] = Crc(r);
        return buf;
    }

    /// <summary>XOR of bytes 2..87, as the device computes it.</summary>
    public static byte Crc(ReadOnlySpan<byte> report)
    {
        byte crc = 0;
        for (int i = 2; i < 88; i++) crc ^= report[i];
        return crc;
    }

    public readonly record struct Reply(bool Ok, bool NoResponse, byte Argument1);

    public static Reply Parse(ReadOnlySpan<byte> withReportId, byte commandClass, byte commandId)
    {
        var r = withReportId[1..];
        bool ours = r[6] == commandClass && r[7] == commandId;
        return r[0] switch
        {
            Success when ours => new Reply(true, false, r[9]),
            // A wireless dongle whose mouse is asleep reports timeout/failure for every query.
            Timeout or Failure or NotSupported when ours => new Reply(false, true, 0),
            _ => new Reply(false, false, 0), // busy, or someone else's reply: retry
        };
    }
}
