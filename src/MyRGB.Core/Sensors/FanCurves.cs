namespace MyRGB.Core.Sensors;

public enum FanMode { Auto, Fixed, Curve }

public readonly record struct CurvePoint(double Temp, double Percent);

/// <summary>How the user wants one fan driven. Saved in settings.</summary>
public sealed record FanSetting
{
    public required string FanId { get; init; }
    public FanMode Mode { get; init; } = FanMode.Auto;
    public double FixedPercent { get; init; } = 50;
    public string SensorKey { get; init; } = SensorKeys.GpuTemp;
    public List<CurvePoint> Points { get; init; } = [.. FanCurve.Balanced];
}

public static class FanCurve
{
    /// <summary>Quiet at idle, ramping through the 60s, flat out by 80 °C.</summary>
    public static IReadOnlyList<CurvePoint> Balanced { get; } =
        [new(30, 30), new(50, 40), new(65, 60), new(75, 80), new(80, 100)];

    /// <summary>The fan speed for a temperature: straight lines between points, flat beyond the ends.</summary>
    public static double Evaluate(IReadOnlyList<CurvePoint> points, double temp)
    {
        if (points.Count == 0) return 100;
        var sorted = points.OrderBy(p => p.Temp).ToList();
        if (temp <= sorted[0].Temp) return sorted[0].Percent;
        for (int i = 1; i < sorted.Count; i++)
        {
            var (a, b) = (sorted[i - 1], sorted[i]);
            if (temp <= b.Temp)
                return b.Temp == a.Temp ? b.Percent : a.Percent + (b.Percent - a.Percent) * (temp - a.Temp) / (b.Temp - a.Temp);
        }
        return sorted[^1].Percent;
    }
}

/// <summary>
/// Drives fans from their settings. The only rules that hold whatever the settings say: flat out
/// once the fan's guard temperature reaches <see cref="EmergencyTemp"/>, and back to the
/// hardware's own control when the temperature a curve follows can't be read. Fans may be set
/// all the way to 0 %; hardware that can't go that low keeps its own minimum. Fans left on Auto
/// are never touched. Call <see cref="ResetAll"/> on the way out.
/// </summary>
public sealed class FanController
{
    public const double Floor = 0;
    public const double EmergencyTemp = 85;
    private static readonly TimeSpan Refresh = TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, (double Percent, DateTime At)> _driven = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The speed MyRGB last asked each fan for, by fan id; absent when the fan is on its own control.</summary>
    public IReadOnlyDictionary<string, double> Targets => _driven.ToDictionary(p => p.Key, p => p.Value.Percent, StringComparer.OrdinalIgnoreCase);

    /// <summary>The speed a fan should run at, or null to leave it to the hardware.</summary>
    public static double? Target(FanSetting? setting, Func<string, double?> read, string guardKey)
    {
        if (setting is null || setting.Mode == FanMode.Auto) return null;
        if (read(guardKey) is >= EmergencyTemp) return 100;

        double wanted;
        if (setting.Mode == FanMode.Fixed) wanted = setting.FixedPercent;
        else if (read(setting.SensorKey) is { } t)
        {
            if (t >= EmergencyTemp) return 100;
            wanted = FanCurve.Evaluate(setting.Points, t);
        }
        else return null; // can't see the temperature: let the hardware decide
        return Math.Clamp(wanted, Floor, 100);
    }

    public void Apply(IReadOnlyList<IFanChannel> fans, IReadOnlyDictionary<string, FanSetting> settings, Func<string, double?> read)
    {
        var now = DateTime.UtcNow;
        foreach (var fan in fans)
        {
            var target = Target(settings.GetValueOrDefault(fan.Id), read, fan.GuardKey);
            if (target is not { } percent)
            {
                if (_driven.Remove(fan.Id)) Try(fan.Reset);
                continue;
            }
            // Resend now and then too: some drivers quietly take a fan back.
            if (_driven.TryGetValue(fan.Id, out var last) && Math.Abs(last.Percent - percent) < 1 && now - last.At < Refresh) continue;
            Try(() => fan.Set(percent));
            _driven[fan.Id] = (percent, now);
        }
    }

    public void ResetAll(IReadOnlyList<IFanChannel> fans)
    {
        foreach (var fan in fans.Where(f => _driven.ContainsKey(f.Id))) Try(fan.Reset);
        _driven.Clear();
    }

    private static void Try(Action action)
    {
        try { action(); }
        catch (Exception) { /* a fan that won't take a command keeps its own control */ }
    }
}
