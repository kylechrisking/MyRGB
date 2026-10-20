namespace MyRGB.Core.Sensors;

public enum SensorKind { Temperature, Load, Clock, Fan, Control, Power, Data, Throughput, Voltage, Other }

/// <summary>What a sensor belongs to, for grouping on the Monitoring page.</summary>
public enum SensorCategory { Cpu, Gpu, Memory, Motherboard, Storage, Network, Other }

/// <summary>
/// One reading. <see cref="Key"/> is set for the few sensors MyRGB uses by name (see
/// <see cref="SensorKeys"/>), so effects and fan curves don't depend on hardware-specific names.
/// </summary>
public sealed record SensorReading(string Id, SensorCategory Category, string Hardware, string Name, SensorKind Kind, double? Value, string? Key = null);

/// <summary>Names for the sensors MyRGB looks up directly. Values are in °C, %, W, GB, MHz or bytes/s.</summary>
public static class SensorKeys
{
    public const string CpuLoad = "cpu.load";
    public const string CpuTemp = "cpu.temp";
    public const string CpuClock = "cpu.clock";
    public const string CpuPower = "cpu.power";
    public const string GpuTemp = "gpu.temp";
    public const string GpuHotspot = "gpu.hotspot";
    public const string GpuMemoryTemp = "gpu.memtemp";
    public const string GpuLoad = "gpu.load";
    public const string GpuPower = "gpu.power";
    public const string GpuClock = "gpu.clock";
    public const string GpuMemoryUsed = "gpu.vram.used";
    public const string GpuMemoryTotal = "gpu.vram.total";
    public const string MemoryLoad = "mem.load";
    public const string MemoryUsed = "mem.used";
    public const string MemoryTotal = "mem.total";
    public const string NetDown = "net.down";
    public const string NetUp = "net.up";

    /// <summary>The sensors a fan curve or the sensor effect can follow, with their names.</summary>
    public static IReadOnlyList<(string Key, string Label)> Followable { get; } =
    [
        (CpuTemp, "CPU temperature"),
        (GpuTemp, "GPU temperature"),
        (GpuHotspot, "GPU hot spot"),
        (CpuLoad, "CPU load"),
        (GpuLoad, "GPU load"),
        (MemoryLoad, "Memory use"),
    ];
}

/// <summary>A fan MyRGB can drive. Values refresh on each <see cref="ISensorSource.Read"/>.</summary>
public interface IFanChannel
{
    string Id { get; }
    string Name { get; }
    /// <summary>The temperature that protects this fan: past <see cref="FanController.EmergencyTemp"/> it runs flat out.</summary>
    string GuardKey { get; }
    double? Rpm { get; }
    /// <summary>The duty cycle it's running at, if the hardware reports it.</summary>
    double? Percent { get; }
    void Set(double percent);
    /// <summary>Hand the fan back to its own (BIOS or driver) control.</summary>
    void Reset();
}

/// <summary>Where readings come from: LibreHardwareMonitor on a real PC, fakes in tests and snapshots.</summary>
public interface ISensorSource : IDisposable
{
    /// <summary>Take a fresh reading of everything. Always called from the same thread.</summary>
    IReadOnlyList<SensorReading> Read();
    IReadOnlyList<IFanChannel> Fans { get; }
}

/// <summary>A source with nothing in it, for when sensors can't be opened.</summary>
public sealed class NoSensors : ISensorSource
{
    public IReadOnlyList<SensorReading> Read() => [];
    public IReadOnlyList<IFanChannel> Fans => [];
    public void Dispose() { }
}

/// <summary>Lets effects read sensors without a reference to the app. Set once by the host.</summary>
public static class SensorFeed
{
    private static volatile Func<string, double?> _get = _ => null;

    public static void Use(Func<string, double?> get) => _get = get;

    public static double? Get(string key) => _get(key);
}
