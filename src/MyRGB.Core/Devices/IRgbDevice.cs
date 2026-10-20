namespace MyRGB.Core.Devices;

public enum DeviceType
{
    Unknown,
    Motherboard,
    Memory,
    Gpu,
    Cooler,
    LedStrip,
    Keyboard,
    Mouse,
    MouseMat,
    Headset,
    HeadsetStand,
    Gamepad,
    Light,
    Speaker,
    Case,
    Microphone,
    Accessory,
    Keypad,
    Monitor,
}

/// <summary>
/// One LED, positioned inside its device's own unit square: (0,0) is the device's top-left,
/// (1,1) its bottom-right. The layout later maps that square onto the shared canvas.
/// </summary>
public readonly record struct LedInfo(string Name, float X, float Y);

/// <summary>A named run of LEDs inside a device, e.g. "Logo" or "Scroll wheel".</summary>
public sealed record ZoneInfo(string Name, int StartIndex, int Count);

/// <summary>A zone whose LED count the hardware can't report, e.g. a fan channel or ARGB header; the user sets it.</summary>
public sealed record ResizableZone(string Name, int LedCount, int Min, int Max);

/// <summary>A saved LED count for one zone of one device. Providers re-apply these on every discovery.</summary>
public sealed record ZoneSize(string DeviceId, string Zone, int LedCount);

/// <summary>A device with user-sized zones. It can have zero <see cref="IRgbDevice.Leds"/> until they are sized.</summary>
public interface IResizableDevice : IRgbDevice
{
    IReadOnlyList<ResizableZone> ResizableZones { get; }
}

/// <summary>
/// A device whose LEDs MyRGB drives directly, one color per LED per frame.
/// Implemented once per transport (OpenRGB SDK today, native HID drivers later).
/// </summary>
public interface IRgbDevice
{
    /// <summary>Stable across restarts and re-plugs; layout and per-device overrides are keyed on it.</summary>
    string Id { get; }

    string Name { get; }
    string Vendor { get; }
    DeviceType Type { get; }

    /// <summary>The <see cref="IDeviceProvider.Id"/> that owns this device.</summary>
    string ProviderId { get; }

    /// <summary>
    /// Identifies the physical hardware across providers, e.g. "usb:1532:00ab". When two providers
    /// report the same key, <see cref="DeviceManager"/> keeps the higher-priority one. Null if unknown.
    /// </summary>
    string? HardwareKey { get; }

    IReadOnlyList<LedInfo> Leds { get; }
    IReadOnlyList<ZoneInfo> Zones { get; }

    /// <summary>The slot it's plugged into, as printed on the board (a RAM stick's "A1"). Null if unknown.</summary>
    string? Slot => null;

    /// <summary>Push one frame. <paramref name="colors"/> has exactly <see cref="Leds"/>.Count entries.</summary>
    ValueTask ApplyAsync(ReadOnlyMemory<Rgb> colors, CancellationToken ct = default);
}
