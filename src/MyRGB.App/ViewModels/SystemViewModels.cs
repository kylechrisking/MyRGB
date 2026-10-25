using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyRGB.App.Services;
using MyRGB.Core.Sensors;

namespace MyRGB.App.ViewModels;

/// <summary>One line on a monitoring card, e.g. "Hot spot  48 °C".</summary>
public sealed partial class MetricLine(string label) : ObservableObject
{
    public string Label => label;
    [ObservableProperty] private string _value = "–";
}

/// <summary>A card on the Monitoring page: one headline value with a chart, and a few details.</summary>
public sealed partial class MetricCard(string title, string key, Color color, double max, params string[] lines) : ObservableObject
{
    public string Title => title;
    public string Key => key;
    public Color Color => color;
    public IBrush Accent { get; } = new SolidColorBrush(color);
    /// <summary>Chart top; 0 fits the chart to the data.</summary>
    public double Max => max;
    public IReadOnlyList<MetricLine> Lines { get; } = lines.Select(l => new MetricLine(l)).ToArray();

    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _value = "–";
    [ObservableProperty] private string _unit = "";
    [ObservableProperty] private float[]? _history;

    public MetricLine Line(string label) => Lines.First(l => l.Label == label);
}

public sealed partial class DriveViewModel : ObservableObject
{
    public required string Name { get; init; }
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _detail = "";
}

public sealed partial class ThreadLoad : ObservableObject
{
    [ObservableProperty] private double _percent;
}

/// <summary>A row in the "every sensor" list.</summary>
public sealed record SensorRow(string Hardware, string Name, string Value);

/// <summary>The Monitoring page: live CPU, GPU, memory, network and drive readings.</summary>
public sealed partial class MonitoringViewModel : ObservableObject
{
    private readonly AppHost _host;

    public MonitoringViewModel(AppHost host)
    {
        _host = host;
        Cards =
        [
            Cpu = new MetricCard("CPU", SensorKeys.CpuLoad, Color.FromRgb(66, 163, 253), 100, "Temperature", "Clock", "Power"),
            Gpu = new MetricCard("GPU", SensorKeys.GpuTemp, Color.FromRgb(86, 219, 143), 100, "Load", "Hot spot", "Power", "Video memory"),
            Memory = new MetricCard("Memory", SensorKeys.MemoryLoad, Color.FromRgb(180, 120, 255), 100, "In use", "Installed"),
            Network = new MetricCard("Network", SensorKeys.NetDown, Color.FromRgb(252, 180, 66), 0, "Upload"),
        ];
    }

    public IReadOnlyList<MetricCard> Cards { get; }
    public MetricCard Cpu { get; }
    public MetricCard Gpu { get; }
    public MetricCard Memory { get; }
    public MetricCard Network { get; }
    public ObservableCollection<ThreadLoad> Threads { get; } = [];
    public ObservableCollection<DriveViewModel> Drives { get; } = [];
    public ObservableCollection<SensorRow> AllSensors { get; } = [];

    [ObservableProperty] private bool _showAll;
    [ObservableProperty] private bool _hasReadings;

    /// <summary>CPU temperatures and motherboard sensors need the elevated helper.</summary>
    [ObservableProperty] private bool _cpuTempMissing;

    partial void OnShowAllChanged(bool value) => Refresh();

    /// <summary>Pull the latest readings into the page. UI thread.</summary>
    public void Refresh()
    {
        var hub = _host.Sensors;
        var latest = hub.Latest;
        HasReadings = latest.Count > 0;
        if (!HasReadings) return;

        string Hardware(SensorCategory c) => latest.FirstOrDefault(r => r.Category == c)?.Hardware ?? "";

        Cpu.Subtitle = Hardware(SensorCategory.Cpu);
        Cpu.Value = Fmt(hub.Get(SensorKeys.CpuLoad), "0");
        Cpu.Unit = "% load";
        Cpu.History = hub.History(SensorKeys.CpuLoad);
        Cpu.Line("Temperature").Value = hub.Get(SensorKeys.CpuTemp) is { } ct ? $"{ct:0} °C" : "needs the helper (see Cooling)";
        Cpu.Line("Clock").Value = hub.Get(SensorKeys.CpuClock) is { } cc ? $"{cc / 1000:0.00} GHz" : "–";
        Cpu.Line("Power").Value = hub.Get(SensorKeys.CpuPower) is > 0 and var cp ? $"{cp:0} W" : "–";
        CpuTempMissing = hub.Get(SensorKeys.CpuTemp) is null;

        // One-line versions for Home: load first, then temperature when there is one.
        CpuSummary = Join(Fmt(hub.Get(SensorKeys.CpuLoad), "0", "%"), hub.Get(SensorKeys.CpuTemp) is { } cpuT ? $"{cpuT:0} °C" : null);
        GpuSummary = Join(Fmt(hub.Get(SensorKeys.GpuLoad), "0", "%"), hub.Get(SensorKeys.GpuTemp) is { } gpuT ? $"{gpuT:0} °C" : null);
        MemorySummary = Fmt(hub.Get(SensorKeys.MemoryLoad), "0", "% used");

        Gpu.Subtitle = Hardware(SensorCategory.Gpu);
        Gpu.Value = Fmt(hub.Get(SensorKeys.GpuTemp), "0");
        Gpu.Unit = "°C";
        Gpu.History = hub.History(SensorKeys.GpuTemp);
        Gpu.Line("Load").Value = Fmt(hub.Get(SensorKeys.GpuLoad), "0", " %");
        Gpu.Line("Hot spot").Value = Fmt(hub.Get(SensorKeys.GpuHotspot), "0", " °C");
        Gpu.Line("Power").Value = Fmt(hub.Get(SensorKeys.GpuPower), "0", " W");
        Gpu.Line("Video memory").Value = hub.Get(SensorKeys.GpuMemoryUsed) is { } vu && hub.Get(SensorKeys.GpuMemoryTotal) is { } vt
            ? $"{vu:0.0} / {vt:0} GB" : "–";

        Memory.Subtitle = "System memory";
        Memory.Value = Fmt(hub.Get(SensorKeys.MemoryLoad), "0");
        Memory.Unit = "% used";
        Memory.History = hub.History(SensorKeys.MemoryLoad);
        Memory.Line("In use").Value = Fmt(hub.Get(SensorKeys.MemoryUsed), "0.0", " GB");
        Memory.Line("Installed").Value = Fmt(hub.Get(SensorKeys.MemoryTotal), "0", " GB");

        var (down, downUnit) = Rate(hub.Get(SensorKeys.NetDown));
        Network.Subtitle = "All adapters";
        Network.Value = down;
        Network.Unit = $"{downUnit} down";
        Network.History = hub.History(SensorKeys.NetDown);
        var (up, upUnit) = Rate(hub.Get(SensorKeys.NetUp));
        Network.Line("Upload").Value = $"{up} {upUnit}";

        var threads = latest.Where(r => r.Category == SensorCategory.Cpu && r.Kind == SensorKind.Load && r.Name.Contains("Thread")).ToList();
        if (threads.Count == 0) threads = latest.Where(r => r.Category == SensorCategory.Cpu && r.Kind == SensorKind.Load && r.Name.StartsWith("CPU Core #")).ToList();
        while (Threads.Count < threads.Count) Threads.Add(new ThreadLoad());
        while (Threads.Count > threads.Count) Threads.RemoveAt(Threads.Count - 1);
        for (int i = 0; i < threads.Count; i++) Threads[i].Percent = threads[i].Value ?? 0;

        var drives = latest.Where(r => r.Category == SensorCategory.Storage && r.Kind == SensorKind.Load).ToList();
        if (drives.Count != Drives.Count || drives.Where((d, i) => d.Hardware != Drives[i].Name).Any())
        {
            Drives.Clear();
            foreach (var d in drives) Drives.Add(new DriveViewModel { Name = d.Hardware });
        }
        for (int i = 0; i < drives.Count; i++)
        {
            var used = latest.FirstOrDefault(r => r.Hardware == drives[i].Hardware && r.Name == "Used space")?.Value;
            var total = latest.FirstOrDefault(r => r.Hardware == drives[i].Hardware && r.Name == "Size")?.Value;
            Drives[i].Percent = drives[i].Value ?? 0;
            Drives[i].Detail = used is { } u && total is { } t ? $"{t - u:0} GB free of {t:0} GB" : "";
        }

        if (ShowAll)
        {
            AllSensors.Clear();
            foreach (var r in latest.Where(r => r.Value is not null).OrderBy(r => r.Category).ThenBy(r => r.Hardware).ThenBy(r => r.Kind))
                AllSensors.Add(new SensorRow(r.Hardware, r.Name, Describe(r)));
        }
    }

    [ObservableProperty] private string _cpuSummary = "–";
    [ObservableProperty] private string _gpuSummary = "–";
    [ObservableProperty] private string _memorySummary = "–";

    private static string Join(string first, string? second) => second is null ? first : $"{first} · {second}";

    private static string Fmt(double? value, string format, string suffix = "") =>
        value is { } v ? v.ToString(format, CultureInfo.CurrentCulture) + suffix : "–";

    private static (string Value, string Unit) Rate(double? bytesPerSecond) => bytesPerSecond switch
    {
        null => ("–", "B/s"),
        >= 1e6 => ((bytesPerSecond.Value / 1e6).ToString("0.0", CultureInfo.CurrentCulture), "MB/s"),
        >= 1e3 => ((bytesPerSecond.Value / 1e3).ToString("0", CultureInfo.CurrentCulture), "KB/s"),
        _ => (bytesPerSecond.Value.ToString("0", CultureInfo.CurrentCulture), "B/s"),
    };

    public static string Describe(SensorReading r) => r.Value is not { } v ? "–" : r.Kind switch
    {
        SensorKind.Temperature => $"{v:0.0} °C",
        SensorKind.Load or SensorKind.Control => $"{v:0} %",
        SensorKind.Clock => $"{v:0} MHz",
        SensorKind.Fan => $"{v:0} RPM",
        SensorKind.Power => $"{v:0.0} W",
        SensorKind.Data => $"{v:0.0} GB",
        SensorKind.Voltage => $"{v:0.000} V",
        SensorKind.Throughput => Rate(v) is var (n, u) ? $"{n} {u}" : "",
        _ => v.ToString("0.##", CultureInfo.CurrentCulture),
    };
}

/// <summary>A temperature shown at the top of the Cooling page.</summary>
public sealed partial class TempGauge(string label, string key) : ObservableObject
{
    public string Label => label;
    public string Key => key;
    [ObservableProperty] private string _value = "–";
    [ObservableProperty] private double _percent;
    [ObservableProperty] private IBrush _brush = Brushes.Gray;
    [ObservableProperty] private bool _available;
}

public sealed record FollowOption(string Key, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One fan on the Cooling page and how it's driven.</summary>
public sealed partial class FanRowViewModel : ObservableObject
{
    public const string AutoMode = "Automatic";
    public const string FixedMode = "Fixed speed";
    public const string CurveMode = "Fan curve";

    private readonly AppHost _host;
    private bool _loading;

    public FanRowViewModel(AppHost host, IFanChannel fan, FanSetting? setting, IReadOnlyList<FollowOption> follow)
    {
        _host = host;
        Id = fan.Id;
        Name = fan.Name;
        Follow = follow;
        _loading = true;
        Mode = setting?.Mode switch { FanMode.Fixed => FixedMode, FanMode.Curve => CurveMode, _ => AutoMode };
        FixedPercent = setting?.FixedPercent ?? 50;
        Points = setting?.Points is { Count: > 1 } p ? p : [.. FanCurve.Balanced];
        Sensor = follow.FirstOrDefault(f => f.Key == (setting?.SensorKey ?? fan.GuardKey)) ?? follow.FirstOrDefault();
        _loading = false;
    }

    public string Id { get; }
    public string Name { get; }
    public IReadOnlyList<string> Modes { get; } = [AutoMode, FixedMode, CurveMode];
    public IReadOnlyList<FollowOption> Follow { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFixed), nameof(IsCurve), nameof(ModeHint))]
    private string _mode = AutoMode;

    [ObservableProperty] private double _fixedPercent;
    [ObservableProperty] private IReadOnlyList<CurvePoint> _points = [];
    [ObservableProperty] private FollowOption? _sensor;
    [ObservableProperty] private string _speed = "–";
    [ObservableProperty] private double? _currentTemp;
    [ObservableProperty] private string _status = "";

    public bool IsFixed => Mode == FixedMode;
    public bool IsCurve => Mode == CurveMode;

    public string ModeHint => Mode switch
    {
        FixedMode => "Runs at one speed all the time.",
        CurveMode => "Speeds up as the temperature rises. Drag the points to shape it; double-click adds one, right-click removes one.",
        _ => Name.Contains("(motherboard)")
            ? "The motherboard's BIOS is deciding this fan's speed for now. MyRGB leaves it alone."
            : "The fan's own controller decides. MyRGB leaves it alone.",
    };

    partial void OnModeChanged(string value) => Save();
    partial void OnFixedPercentChanged(double value) => Save();
    partial void OnPointsChanged(IReadOnlyList<CurvePoint> value) => Save();
    partial void OnSensorChanged(FollowOption? value) => Save();

    [RelayCommand] private void Silent() => Points = [new(25, 0), new(40, 15), new(55, 35), new(70, 65), new(78, 100)];
    [RelayCommand] private void Quiet() => Points = [new(30, 30), new(55, 35), new(70, 55), new(78, 80), new(83, 100)];
    [RelayCommand] private void Balanced() => Points = [.. FanCurve.Balanced];
    [RelayCommand] private void Performance() => Points = [new(30, 45), new(45, 55), new(60, 75), new(70, 90), new(78, 100)];
    [RelayCommand] private void FullSpeed() => Points = [new(25, 100)];

    private void Save()
    {
        if (_loading) return;
        _host.SetFan(new FanSetting
        {
            FanId = Id,
            Mode = Mode switch { FixedMode => FanMode.Fixed, CurveMode => FanMode.Curve, _ => FanMode.Auto },
            FixedPercent = FixedPercent,
            SensorKey = Sensor?.Key ?? SensorKeys.GpuTemp,
            Points = [.. Points],
        });
    }

    public void Refresh(IFanChannel fan, double? target)
    {
        Speed = fan.Rpm is { } rpm
            ? rpm < 1 ? "Stopped" : $"{rpm:0} RPM"
            : fan.Percent is { } p ? $"{p:0} %" : "–";
        CurrentTemp = Sensor is { } s ? _host.Sensors.Get(s.Key) : null;
        Status = target is { } t ? $"MyRGB is running it at {t:0} %" : Mode == AutoMode ? "On automatic" : "Waiting for a temperature reading";
    }
}

/// <summary>The Cooling page: temperatures, and fans with fixed speeds or curves.</summary>
public sealed partial class CoolingViewModel : ObservableObject
{
    private readonly AppHost _host;

    public CoolingViewModel(AppHost host)
    {
        _host = host;
        Temps =
        [
            new TempGauge("CPU", SensorKeys.CpuTemp),
            new TempGauge("GPU", SensorKeys.GpuTemp),
            new TempGauge("GPU hot spot", SensorKeys.GpuHotspot),
            new TempGauge("GPU memory", SensorKeys.GpuMemoryTemp),
        ];
    }

    public IReadOnlyList<TempGauge> Temps { get; }
    public ObservableCollection<FanRowViewModel> Fans { get; } = [];

    [ObservableProperty] private bool _hasFans;
    [ObservableProperty] private string _adminStatus = "";

    /// <summary>True while MyRGB runs without admin rights.</summary>
    public bool NeedsAdmin { get; init; } = !Program.IsElevated;

    /// <summary>Whether the elevated sensor helper is answering; temperatures appear once it is.</summary>
    [ObservableProperty] private bool _helperConnected;

    /// <summary>The helper is connected but older than this MyRGB; one more prompt brings it current.</summary>
    [ObservableProperty] private bool _helperOutdated;

    [RelayCommand]
    private void RestartAsAdmin() =>
        AdminStatus = Program.RestartElevated() ? "Opening MyRGB as administrator…" : "Windows didn't give permission, so MyRGB is still running without it.";

    /// <summary>
    /// One Windows prompt installs and starts the sensor helper: a tiny service that runs as
    /// administrator and reads temperatures for MyRGB, which carries on without any. It comes
    /// back on its own after a restart, and uninstalling MyRGB removes it.
    /// </summary>
    [RelayCommand]
    private void EnableHelper()
    {
        var script = Path.Combine(AppContext.BaseDirectory, "setup", "setup-openrgb-admin.ps1");
        if (!File.Exists(script))
        {
            AdminStatus = "The setup script is missing from MyRGB's folder; reinstall MyRGB to add it.";
            return;
        }
        AdminStatus = Program.Elevate("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" " +
            $"-InstallSensors \"{AppContext.BaseDirectory.TrimEnd('\\')}\" -User \"{Environment.UserDomainName}\\{Environment.UserName}\"")
            ? "Permission granted; CPU temperatures and the fans appear here within a few seconds."
            : "Windows didn't give permission, so nothing was changed.";
    }

    /// <summary>Undo the helper: one prompt, then MyRGB goes back to reading what it can without admin.</summary>
    [RelayCommand]
    private void TurnOffHelper()
    {
        var script = Path.Combine(AppContext.BaseDirectory, "setup", "setup-openrgb-admin.ps1");
        if (!File.Exists(script))
        {
            AdminStatus = "The setup script is missing from MyRGB's folder; reinstall MyRGB to add it.";
            return;
        }
        AdminStatus = Program.Elevate("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" -RemoveSensors")
            ? "The helper was removed; MyRGB is back to reading what it can without administrator rights."
            : "Windows didn't give permission, so the helper is still running.";
    }

    public string SafetyText =>
        $"MyRGB only drives fans you set to a fixed speed or a curve, and they may go all the way down to 0 % - some fans won't spin below their own minimum. Any fan runs flat out once its temperature reaches {FanController.EmergencyTemp:0} °C, and every fan goes back to automatic when MyRGB closes.";

    public void Refresh()
    {
        var hub = _host.Sensors;
        HelperConnected = _host.SensorHelperConnected;
        HelperOutdated = HelperConnected
            && Version.TryParse(_host.SensorHelperVersion ?? "", out var helperVersion)
            && helperVersion < UpdateViewModel.CurrentVersion;
        foreach (var t in Temps)
        {
            var v = hub.Get(t.Key);
            t.Available = v is not null;
            t.Value = v is { } c ? $"{c:0}°" : "–";
            t.Percent = v is { } p ? Math.Clamp(p, 0, 100) : 0;
            t.Brush = new SolidColorBrush(v switch
            {
                >= 80 => Color.FromRgb(247, 93, 89),
                >= 65 => Color.FromRgb(252, 180, 66),
                _ => Color.FromRgb(86, 219, 143),
            });
        }

        var fans = hub.Fans;
        if (fans.Count != Fans.Count || fans.Where((f, i) => f.Id != Fans[i].Id).Any())
        {
            var follow = SensorKeys.Followable.Where(f => f.Key.EndsWith("temp") || f.Key == SensorKeys.GpuHotspot)
                .Where(f => hub.Get(f.Key) is not null)
                .Select(f => new FollowOption(f.Key, f.Label)).ToList();
            var settings = _host.Settings.Fans.ToDictionary(s => s.FanId, StringComparer.OrdinalIgnoreCase);
            Fans.Clear();
            foreach (var f in fans) Fans.Add(new FanRowViewModel(_host, f, settings.GetValueOrDefault(f.Id), follow));
            HasFans = Fans.Count > 0;
        }
        var targets = hub.FanTargets;
        for (int i = 0; i < fans.Count; i++) Fans[i].Refresh(fans[i], targets.TryGetValue(fans[i].Id, out var t2) ? t2 : null);

        int driven = Fans.Count(f => f.Mode != FanRowViewModel.AutoMode);
        FanSummary = Fans.Count == 0 ? "No fans found yet"
            : $"{Fans.Count} fan{(Fans.Count == 1 ? "" : "s")} · {(driven == 0 ? "all on automatic" : $"{driven} set by MyRGB")}";
        var hottest = Temps.Where(t => t.Available).MaxBy(t => hub.Get(t.Key));
        Hottest = hottest is null ? "–" : $"{hottest.Value}";
        HottestLabel = hottest is null ? "No temperatures yet" : $"Hottest: {hottest.Label}";
        HottestBrush = hottest?.Brush ?? Brushes.Gray;
    }

    // Home's summary card.
    [ObservableProperty] private string _fanSummary = "";
    [ObservableProperty] private string _hottest = "–";
    [ObservableProperty] private string _hottestLabel = "";
    [ObservableProperty] private IBrush _hottestBrush = Brushes.Gray;
}
