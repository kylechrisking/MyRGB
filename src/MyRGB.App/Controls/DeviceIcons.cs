using Avalonia.Media;
using MyRGB.Core.Devices;

namespace MyRGB.App.Controls;

/// <summary>A 24x24 glyph and a plain-English name for each kind of device.</summary>
public static class DeviceIcons
{
    private static readonly Dictionary<string, Geometry> Cache = new();

    private static Geometry G(string data)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(data, out var g)) Cache[data] = g = StreamGeometry.Parse(data);
            return g;
        }
    }

    private const string Keyboard = "F0 M2,6 H22 V18 H2 Z M3.5,7.5 V16.5 H20.5 V7.5 Z M5,9 H7 V11 H5 Z M8.5,9 H10.5 V11 H8.5 Z M12,9 H14 V11 H12 Z M15.5,9 H17.5 V11 H15.5 Z M7,13 H17 V15 H7 Z";
    private const string Mouse = "F0 M12,2 C8.5,2 6,4.6 6,8 V15 C6,19 8.7,22 12,22 C15.3,22 18,19 18,15 V8 C18,4.6 15.5,2 12,2 Z M11.2,5 H12.8 V9.5 H11.2 Z";
    private const string Headset = "M12,3 C7,3 3,7 3,12 V18 C3,19.7 4.3,21 6,21 H8 V13 H5 V12 C5,8.1 8.1,5 12,5 C15.9,5 19,8.1 19,12 V13 H16 V21 H18 C19.7,21 21,19.7 21,18 V12 C21,7 17,3 12,3 Z";
    private const string Stand = "M7,3 H17 V5.5 H13.2 V18 H10.8 V5.5 H7 Z M5,19 H19 V21.5 H5 Z";
    private const string Memory = "F0 M2,6 H22 V15 H2 Z M4,8 H8 V13 H4 Z M10,8 H14 V13 H10 Z M16,8 H20 V13 H16 Z M4,16 H5.5 V19 H4 Z M8,16 H9.5 V19 H8 Z M12,16 H13.5 V19 H12 Z M16,16 H17.5 V19 H16 Z";
    private const string Board = "F0 M3,3 H21 V21 H3 Z M5,5 V19 H19 V5 Z M7,7 H12 V12 H7 Z M14,7 H17 V9 H14 Z M14,11 H17 V13 H14 Z M7,15 H17 V17 H7 Z";
    private const string Fan = "F0 M12,2 A10,10 0 1 1 11.99,2 Z M12,4.2 A7.8,7.8 0 1 0 12.01,4.2 Z M12,9 A3,3 0 1 1 11.99,9 Z M11,3 H13 V9 H11 Z M11,15 H13 V21 H11 Z M3,11 H9 V13 H3 Z M15,11 H21 V13 H15 Z";
    private const string Gpu = "F0 M2,7 H22 V17 H2 Z M4,9 V15 H13 V9 Z M17.5,9.5 A2.5,2.5 0 1 1 17.49,9.5 Z M4,18 H8 V20 H4 Z";
    private const string Light = "M12,3 A7,7 0 0 1 16,15.7 V18 H8 V15.7 A7,7 0 0 1 12,3 Z M9,19.5 H15 V21.5 H9 Z";

    public static Geometry For(DeviceType type) => G(type switch
    {
        DeviceType.Keyboard or DeviceType.Keypad => Keyboard,
        DeviceType.Mouse or DeviceType.MouseMat => Mouse,
        DeviceType.Headset or DeviceType.Speaker or DeviceType.Microphone => Headset,
        DeviceType.HeadsetStand => Stand,
        DeviceType.Memory => Memory,
        DeviceType.Motherboard => Board,
        DeviceType.Cooler or DeviceType.LedStrip or DeviceType.Case => Fan,
        DeviceType.Gpu => Gpu,
        _ => Light,
    });

    public static string Label(DeviceType type) => type switch
    {
        DeviceType.HeadsetStand => "Headset stand",
        DeviceType.Memory => "RAM",
        DeviceType.LedStrip => "Fan / LED controller",
        DeviceType.Gpu => "Graphics card",
        DeviceType.MouseMat => "Mouse mat",
        DeviceType.Unknown => "Device",
        _ => type.ToString(),
    };
}
