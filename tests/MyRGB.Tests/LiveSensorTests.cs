using MyRGB.Core.Sensors;
using MyRGB.Sensors;

namespace MyRGB.Tests;

/// <summary>Reads this PC's real sensors. Explicit; it never touches the fans.</summary>
public class LiveSensorTests(ITestOutputHelper output)
{
    [Fact(Explicit = true)]
    public void Reads_named_sensors()
    {
        using var hub = new SensorHub(new HardwareSensors());
        hub.Poll();
        Thread.Sleep(1000);
        hub.Poll();

        foreach (var (key, label) in SensorKeys.Followable.Concat([(SensorKeys.CpuClock, "CPU clock"), (SensorKeys.GpuPower, "GPU power"),
                     (SensorKeys.MemoryUsed, "Memory used"), (SensorKeys.MemoryTotal, "Memory total"), (SensorKeys.NetDown, "Download")]))
            output.WriteLine($"{label,-16} {hub.Get(key)?.ToString("0.0") ?? "-"}");
        foreach (var fan in hub.Fans) output.WriteLine($"fan {fan.Name}: {fan.Rpm} rpm, {fan.Percent} %");
        Assert.NotNull(hub.Get(SensorKeys.CpuLoad));
    }
}
