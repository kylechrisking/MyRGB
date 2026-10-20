using System.Net.NetworkInformation;
using LibreHardwareMonitor.Hardware;
using MyRGB.Core.Sensors;

namespace MyRGB.Sensors;

/// <summary>
/// The PC's own sensors, through LibreHardwareMonitor, plus drive space and network traffic from
/// Windows. Without admin rights LibreHardwareMonitor still sees CPU load, memory and the GPU
/// (temperatures, power, fans); CPU temperatures and motherboard fans need it to run elevated.
/// </summary>
public sealed class HardwareSensors : ISensorSource
{
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true,
        IsMotherboardEnabled = true,
        IsControllerEnabled = true,
    };

    private bool _opened;
    private List<LhmFan> _fans = [];
    private long _rx, _tx;
    private DateTime _netAt;

    public IReadOnlyList<IFanChannel> Fans => _fans;

    public IReadOnlyList<SensorReading> Read()
    {
        if (!_opened)
        {
            _computer.Open();
            _opened = true;
        }

        var readings = new List<SensorReading>();
        var fans = new List<LhmFan>();
        foreach (var hw in _computer.Hardware) Visit(hw, readings, fans);
        AddCpuClock(readings);
        AddDrives(readings);
        AddNetwork(readings);

        // Keep the same fan objects between reads, so their ids and state stay put.
        if (fans.Count != _fans.Count || fans.Where((f, i) => f.Id != _fans[i].Id).Any()) _fans = fans;
        else for (int i = 0; i < fans.Count; i++) _fans[i].Refresh(fans[i]);
        return readings;
    }

    private static void Visit(IHardware hw, List<SensorReading> readings, List<LhmFan> fans)
    {
        hw.Update();
        var category = Categorize(hw.HardwareType);
        foreach (var s in hw.Sensors)
        {
            var kind = Kind(s.SensorType);
            double? value = s.Value is { } v && !float.IsNaN(v) ? v : null;
            // The GPU reports video memory in MB; MyRGB shows GB.
            if (s.SensorType == SensorType.SmallData && value is { } mb) { value = mb / 1024; kind = SensorKind.Data; }
            readings.Add(new SensorReading(s.Identifier.ToString(), category, hw.Name, s.Name, kind, value, KeyFor(hw, s, category)));

            if (s.SensorType == SensorType.Control && s.Control is { } control)
            {
                var rpm = hw.Sensors.FirstOrDefault(f => f.SensorType == SensorType.Fan && f.Index == s.Index);
                fans.Add(new LhmFan(s.Identifier.ToString(), FanName(hw, s), category == SensorCategory.Gpu ? SensorKeys.GpuTemp : SensorKeys.CpuTemp,
                    control, rpm?.Value, value));
            }
        }
        foreach (var sub in hw.SubHardware) Visit(sub, readings, fans);
    }

    private static string FanName(IHardware hw, ISensor s) =>
        hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel
            ? s.Name.Replace("GPU Fan", "GPU fan")
            : $"{s.Name} (motherboard)";

    private static string? KeyFor(IHardware hw, ISensor s, SensorCategory category) => (category, s.SensorType, s.Name) switch
    {
        (SensorCategory.Cpu, SensorType.Load, "CPU Total") => SensorKeys.CpuLoad,
        (SensorCategory.Cpu, SensorType.Temperature, "CPU Package" or "Core (Tctl/Tdie)") => SensorKeys.CpuTemp,
        (SensorCategory.Cpu, SensorType.Power, "CPU Package") => SensorKeys.CpuPower,
        (SensorCategory.Gpu, SensorType.Temperature, "GPU Core") => SensorKeys.GpuTemp,
        (SensorCategory.Gpu, SensorType.Temperature, "GPU Hot Spot") => SensorKeys.GpuHotspot,
        (SensorCategory.Gpu, SensorType.Temperature, "GPU Memory Junction") => SensorKeys.GpuMemoryTemp,
        (SensorCategory.Gpu, SensorType.Load, "GPU Core") => SensorKeys.GpuLoad,
        (SensorCategory.Gpu, SensorType.Power, "GPU Package") => SensorKeys.GpuPower,
        (SensorCategory.Gpu, SensorType.Clock, "GPU Core") => SensorKeys.GpuClock,
        (SensorCategory.Gpu, SensorType.SmallData, "GPU Memory Used") => SensorKeys.GpuMemoryUsed,
        (SensorCategory.Gpu, SensorType.SmallData, "GPU Memory Total") => SensorKeys.GpuMemoryTotal,
        (SensorCategory.Memory, SensorType.Load, "Memory") when !hw.Name.Contains("Virtual") => SensorKeys.MemoryLoad,
        (SensorCategory.Memory, SensorType.Data, "Memory Used") when !hw.Name.Contains("Virtual") => SensorKeys.MemoryUsed,
        _ => null,
    };

    /// <summary>One CPU clock (the cores' average), and total memory, which the library reports as used + free.</summary>
    private static void AddCpuClock(List<SensorReading> readings)
    {
        var clocks = readings.Where(r => r.Category == SensorCategory.Cpu && r.Kind == SensorKind.Clock && r.Name.StartsWith("CPU Core") && r.Value > 0)
            .Select(r => r.Value!.Value).ToList();
        if (clocks.Count > 0)
            readings.Add(new SensorReading("myrgb/cpu/clock", SensorCategory.Cpu, "CPU", "Clock", SensorKind.Clock, clocks.Average(), SensorKeys.CpuClock));

        var used = readings.FirstOrDefault(r => r.Key == SensorKeys.MemoryUsed)?.Value;
        var free = readings.FirstOrDefault(r => r.Category == SensorCategory.Memory && r.Name == "Memory Available" && !r.Hardware.Contains("Virtual"))?.Value;
        if (used is { } u && free is { } f)
            readings.Add(new SensorReading("myrgb/memory/total", SensorCategory.Memory, "Memory", "Memory Total", SensorKind.Data, u + f, SensorKeys.MemoryTotal));
    }

    private static void AddDrives(List<SensorReading> readings)
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady || d.TotalSize <= 0) continue;
                string letter = d.Name.TrimEnd('\\');
                string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? letter : $"{letter} {d.VolumeLabel}";
                double total = d.TotalSize / 1e9, used = (d.TotalSize - d.TotalFreeSpace) / 1e9;
                readings.Add(new SensorReading($"myrgb/drive/{letter}/load", SensorCategory.Storage, label, "Used", SensorKind.Load, used / total * 100));
                readings.Add(new SensorReading($"myrgb/drive/{letter}/used", SensorCategory.Storage, label, "Used space", SensorKind.Data, used));
                readings.Add(new SensorReading($"myrgb/drive/{letter}/total", SensorCategory.Storage, label, "Size", SensorKind.Data, total));
            }
            catch (IOException) { /* drive went away between listing and reading */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void AddNetwork(List<SensorReading> readings)
    {
        long rx = 0, tx = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var stats = nic.GetIPStatistics();
            rx += stats.BytesReceived;
            tx += stats.BytesSent;
        }
        var now = DateTime.UtcNow;
        double seconds = (now - _netAt).TotalSeconds;
        double? down = null, up = null;
        if (_netAt != default && seconds > 0 && rx >= _rx && tx >= _tx)
        {
            down = (rx - _rx) / seconds;
            up = (tx - _tx) / seconds;
        }
        (_rx, _tx, _netAt) = (rx, tx, now);
        readings.Add(new SensorReading("myrgb/net/down", SensorCategory.Network, "Network", "Download", SensorKind.Throughput, down, SensorKeys.NetDown));
        readings.Add(new SensorReading("myrgb/net/up", SensorCategory.Network, "Network", "Upload", SensorKind.Throughput, up, SensorKeys.NetUp));
    }

    private static SensorCategory Categorize(HardwareType type) => type switch
    {
        HardwareType.Cpu => SensorCategory.Cpu,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => SensorCategory.Gpu,
        HardwareType.Memory => SensorCategory.Memory,
        HardwareType.Motherboard or HardwareType.SuperIO or HardwareType.EmbeddedController => SensorCategory.Motherboard,
        HardwareType.Storage => SensorCategory.Storage,
        HardwareType.Network => SensorCategory.Network,
        _ => SensorCategory.Other,
    };

    private static SensorKind Kind(SensorType type) => type switch
    {
        SensorType.Temperature => SensorKind.Temperature,
        SensorType.Load => SensorKind.Load,
        SensorType.Clock => SensorKind.Clock,
        SensorType.Fan => SensorKind.Fan,
        SensorType.Control => SensorKind.Control,
        SensorType.Power => SensorKind.Power,
        SensorType.Data or SensorType.SmallData => SensorKind.Data,
        SensorType.Throughput => SensorKind.Throughput,
        SensorType.Voltage => SensorKind.Voltage,
        _ => SensorKind.Other,
    };

    public void Dispose()
    {
        if (_opened) _computer.Close();
    }

    private sealed class LhmFan(string id, string name, string guardKey, IControl control, float? rpm, double? percent) : IFanChannel
    {
        private IControl _control = control;

        public string Id => id;
        public string Name => name;
        public string GuardKey => guardKey;
        public double? Rpm { get; private set; } = rpm;
        public double? Percent { get; private set; } = percent;

        public void Refresh(LhmFan fresh)
        {
            _control = fresh._control;
            Rpm = fresh.Rpm;
            Percent = fresh.Percent;
        }

        public void Set(double value) => _control.SetSoftware((float)Math.Clamp(value, 0, 100));
        public void Reset() => _control.SetDefault();
    }
}
