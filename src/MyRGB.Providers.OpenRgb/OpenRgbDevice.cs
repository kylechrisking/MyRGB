using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MyRGB.Core;
using MyRGB.Core.Devices;

namespace MyRGB.Providers.OpenRgb;

internal sealed partial class OpenRgbDevice : IResizableDevice
{
    private readonly OpenRgbClient _client;
    private readonly uint _index;

    public OpenRgbDevice(OpenRgbClient client, uint index, ControllerData data)
    {
        _client = client;
        _index = index;
        Data = data;
        Name = CleanName(data.Name);
        Vendor = string.IsNullOrWhiteSpace(data.Vendor) ? "Unknown" : data.Vendor;
        Type = MapType(data.Type);
        Id = MakeId(data);
        HardwareKey = UsbKey(data.Location);
        Slot = Type == DeviceType.Memory ? MemorySlots.FromLocation(data.Location) : null;
        (Leds, Zones) = LedLayout.Build(data);
        ResizableZones = data.Zones.Where(z => z.Resizable)
            .Select(z => new ResizableZone(z.Name, (int)z.LedCount, (int)z.LedsMin, (int)z.LedsMax))
            .ToArray();
    }

    /// <summary>
    /// Some devices report their name doubled ("G733 Gaming HeadG733 Gaming Head"), so the two
    /// halves are the same text. Show just one copy; the id keeps the raw name so saved layouts
    /// and settings survive the cleanup.
    /// </summary>
    internal static string CleanName(string name)
    {
        var trimmed = name.Trim();
        int half = trimmed.Length / 2;
        if (half > 0 && trimmed.Length % 2 == 0 &&
            trimmed[..half].Equals(trimmed[half..], StringComparison.OrdinalIgnoreCase))
            return trimmed[..half].Trim();
        return trimmed;
    }

    /// <summary>Doesn't depend on LED counts, so resizing a zone keeps the device's layout and overrides.</summary>
    internal static string MakeId(ControllerData data)
    {
        var vendor = string.IsNullOrWhiteSpace(data.Vendor) ? "Unknown" : data.Vendor;
        return $"openrgb:{Slug(vendor)}:{Slug(data.Name)}:{ShortHash(string.IsNullOrWhiteSpace(data.Serial) ? data.Location : data.Serial)}";
    }

    public ControllerData Data { get; }
    public string Id { get; }
    public string Name { get; }
    public string Vendor { get; }
    public DeviceType Type { get; }
    public string ProviderId => OpenRgbProvider.ProviderId;
    public string? HardwareKey { get; }
    public string? Slot { get; }
    public IReadOnlyList<LedInfo> Leds { get; }
    public IReadOnlyList<ZoneInfo> Zones { get; }
    public IReadOnlyList<ResizableZone> ResizableZones { get; }

    public ValueTask ApplyAsync(ReadOnlyMemory<Rgb> colors, CancellationToken ct = default) =>
        colors.IsEmpty ? ValueTask.CompletedTask : _client.UpdateLedsAsync(_index, colors, ct);

    // OpenRGB's RGBController device_type enum, in order.
    private static DeviceType MapType(int t) => t switch
    {
        0 => DeviceType.Motherboard,
        1 => DeviceType.Memory,
        2 => DeviceType.Gpu,
        3 => DeviceType.Cooler,
        4 => DeviceType.LedStrip,
        5 => DeviceType.Keyboard,
        6 => DeviceType.Mouse,
        7 => DeviceType.MouseMat,
        8 => DeviceType.Headset,
        9 => DeviceType.HeadsetStand,
        10 => DeviceType.Gamepad,
        11 => DeviceType.Light,
        12 => DeviceType.Speaker,
        14 => DeviceType.Accessory, // storage
        15 => DeviceType.Case,
        16 => DeviceType.Microphone,
        17 => DeviceType.Accessory,
        18 => DeviceType.Keypad,
        20 => DeviceType.Monitor,
        _ => DeviceType.Unknown,
    };

    /// <summary>"usb:1532:00ab" from a HID location like "HID: \\?\hid#vid_1532&amp;pid_00ab&amp;mi_02#...".</summary>
    internal static string? UsbKey(string location)
    {
        var m = VidPid().Match(location);
        return m.Success ? $"usb:{m.Groups[1].Value.ToLowerInvariant()}:{m.Groups[2].Value.ToLowerInvariant()}" : null;
    }

    private static string Slug(string s) =>
        new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    private static string ShortHash(string s) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..8];

    [GeneratedRegex(@"vid_([0-9a-f]{4}).*?pid_([0-9a-f]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VidPid();
}

/// <summary>Turns OpenRGB zones (single, linear, matrix) into LED positions inside the device's unit square.</summary>
internal static class LedLayout
{
    public static (IReadOnlyList<LedInfo> Leds, IReadOnlyList<ZoneInfo> Zones) Build(ControllerData data)
    {
        int total = data.LedNames.Count;
        var x = new float[total];
        var y = new float[total];
        Array.Fill(x, 0.5f);
        Array.Fill(y, 0.5f);

        // Stack zones top to bottom; a matrix zone gets as many rows as its matrix is tall.
        var weights = data.Zones.Select(z => z.Matrix is not null ? Math.Max(1f, z.MatrixHeight) : 1f).ToArray();
        float totalWeight = Math.Max(1, weights.Sum());
        var zones = new List<ZoneInfo>();
        int start = 0;
        float bandTop = 0;

        for (int zi = 0; zi < data.Zones.Count; zi++)
        {
            var z = data.Zones[zi];
            int count = (int)Math.Min(z.LedCount, (uint)Math.Max(0, total - start));
            float band = weights[zi] / totalWeight;
            if (count > 0) zones.Add(new ZoneInfo(z.Name, start, count));

            if (z.Matrix is { } map && z.MatrixWidth > 0)
            {
                for (int row = 0; row < z.MatrixHeight; row++)
                for (int col = 0; col < z.MatrixWidth; col++)
                {
                    uint led = map[row * z.MatrixWidth + col];
                    if (led >= count) continue; // 0xFFFFFFFF marks an empty cell
                    x[start + led] = z.MatrixWidth == 1 ? 0.5f : col / (float)(z.MatrixWidth - 1);
                    y[start + led] = bandTop + band * (z.MatrixHeight == 1 ? 0.5f : row / (float)(z.MatrixHeight - 1));
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    x[start + i] = count == 1 ? 0.5f : i / (float)(count - 1);
                    y[start + i] = bandTop + band / 2;
                }
            }

            start += count;
            bandTop += band;
        }

        var leds = Enumerable.Range(0, total).Select(i => new LedInfo(data.LedNames[i], x[i], y[i])).ToArray();
        return (leds, zones);
    }
}
