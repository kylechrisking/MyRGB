using MyRGB.Core.Devices;
using MyRGB.Hid;

namespace MyRGB.Battery;

/// <summary>
/// Logitech wireless headsets and mice over HID++ 2.0 (what G HUB uses). Each capability is a
/// numbered "feature"; we ask the device which index it gave the battery feature, then query it.
/// </summary>
public sealed class LogitechBatteryProvider : IBatteryProvider
{
    private static readonly Dictionary<ushort, string> Known = new()
    {
        [0x0AB5] = "Logitech G733",
        [0x0AFE] = "Logitech G733",
        [0x0A87] = "Logitech G935",
        [0x0A5B] = "Logitech G933",
        [0x0AAF] = "Logitech G915",
        [0x0ACB] = "Logitech G435",
        [0x0AF7] = "Logitech PRO X 2",
        [0xC539] = "Logitech Lightspeed receiver",
        [0xC547] = "Logitech Lightspeed receiver",
    };

    public IReadOnlyList<IBatterySource> Discover()
    {
        var sources = new List<IBatterySource>();
        foreach (var (pid, name) in Known)
        {
            // HID++ lives on a vendor collection (usage page 0xFF43 or 0xFF00) with 20-byte "long" reports.
            var iface = HidDevices.Find($"vid_046d&pid_{pid:x4}")
                .FirstOrDefault(i => i.UsagePage is 0xFF43 or 0xFF00 && i.OutputReportLength == HidPP.LongLength);
            if (iface is not null) sources.Add(new LogitechBatterySource(iface.Path, name, $"usb:046d:{pid:x4}"));
        }
        return sources;
    }
}

internal sealed class LogitechBatterySource(string path, string name, string hardwareKey) : IBatterySource
{
    // Battery features in order of preference: Unified Battery, Battery Status, ADC voltage (headsets).
    private const ushort UnifiedBatteryFeature = 0x1004, BatteryStatusFeature = 0x1000, AdcMeasurementFeature = 0x1F20;

    private (ushort Feature, byte Index)? _feature;

    public string HardwareKey => hardwareKey;
    public string Name => name;

    public async Task<BatteryStatus?> ReadAsync(CancellationToken ct)
    {
        await using var stream = HidDevices.Open(path);
        var hidpp = new HidPP(stream);

        _feature ??= await FindBatteryFeature(hidpp, ct);
        if (_feature is not { } f) return null;

        switch (f.Feature)
        {
            case UnifiedBatteryFeature:
            {
                var r = await hidpp.Call(f.Index, function: 1, [], ct);
                if (r is null) return null;
                // [0] state of charge %, [2] charging status (0 discharging, 1-2 charging, 3 full).
                return new BatteryStatus(Math.Clamp((int)r[0], 0, 100), r[2] is 1 or 2);
            }
            case BatteryStatusFeature:
            {
                var r = await hidpp.Call(f.Index, function: 0, [], ct);
                if (r is null) return null;
                // [0] percent, [2] status (0 discharging, 1 recharging, 2 almost full, 3 full).
                return new BatteryStatus(Math.Clamp((int)r[0], 0, 100), r[2] is 1 or 2);
            }
            default:
            {
                var r = await hidpp.Call(f.Index, function: 0, [], ct);
                if (r is null) return null;
                int millivolts = (r[0] << 8) | r[1];
                if (millivolts == 0) return null; // headset off: the dongle answers with zeros
                // [2] flags: 0x03 = charging (cable in), 0x01 = on battery.
                return new BatteryStatus(VoltageToPercent(millivolts), r[2] == 0x03, Estimated: true);
            }
        }
    }

    private static async Task<(ushort, byte)?> FindBatteryFeature(HidPP hidpp, CancellationToken ct)
    {
        foreach (var feature in new[] { UnifiedBatteryFeature, BatteryStatusFeature, AdcMeasurementFeature })
        {
            // Root feature (index 0), function 0 = getFeature(featureId) → [0] its index, 0 if absent.
            var r = await hidpp.Call(0x00, function: 0, [(byte)(feature >> 8), (byte)feature], ct);
            if (r is { } reply && reply[0] != 0) return (feature, reply[0]);
            if (r is null) return null; // device unreachable; try again next poll
        }
        return null;
    }

    /// <summary>
    /// Typical single-cell Li-ion discharge curve under light load, interpolated. Logitech headsets
    /// only report voltage, so this is an estimate (G HUB does the same with its own table).
    /// </summary>
    internal static int VoltageToPercent(int mv)
    {
        (int Mv, int Pct)[] curve = [(4150, 100), (4050, 90), (3970, 80), (3900, 70), (3840, 60), (3790, 50), (3750, 40), (3710, 30), (3670, 20), (3620, 10), (3500, 3), (3400, 0)];
        if (mv >= curve[0].Mv) return 100;
        for (int i = 1; i < curve.Length; i++)
        {
            if (mv >= curve[i].Mv)
            {
                var (hiMv, hiPct) = curve[i - 1];
                var (loMv, loPct) = curve[i];
                return loPct + (int)Math.Round((mv - loMv) / (double)(hiMv - loMv) * (hiPct - loPct));
            }
        }
        return 0;
    }
}

/// <summary>Minimal HID++ 2.0 request/response over the long (20-byte) report.</summary>
internal sealed class HidPP(FileStream stream)
{
    public const int LongLength = 20;
    private const byte LongReportId = 0x11, DeviceIndex = 0xFF, SoftwareId = 0x0B, ErrorFeature = 0xFF;

    /// <summary>Call a feature function. Returns the 16 parameter bytes of the reply, or null on error/timeout.</summary>
    public async Task<byte[]?> Call(byte featureIndex, int function, byte[] args, CancellationToken ct)
    {
        var req = new byte[LongLength];
        req[0] = LongReportId;
        req[1] = DeviceIndex;
        req[2] = featureIndex;
        req[3] = (byte)((function << 4) | SoftwareId);
        args.CopyTo(req, 4);
        await stream.WriteAsync(req, ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(800));
        var buf = new byte[LongLength];
        try
        {
            while (true)
            {
                int n = await stream.ReadAsync(buf, timeout.Token);
                if (n < 7 || buf[0] != LongReportId) continue; // short reports and other traffic
                // Error reply: feature 0xFF, then the original feature index and function/sw id.
                if (buf[2] == ErrorFeature && buf[3] == featureIndex && buf[4] == req[3]) return null;
                if (buf[2] == featureIndex && buf[3] == req[3]) return buf[4..];
                // Anything else is G HUB's traffic or a notification; keep reading.
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
