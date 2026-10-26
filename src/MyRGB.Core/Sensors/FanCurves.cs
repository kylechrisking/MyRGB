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

    /// <summary>How a generated curve climbs from its start to full speed.</summary>
    public enum CurveShape
    {
        /// <summary>Stays low for a while, then ramps hard near the top.</summary>
        Gentle,
        /// <summary>Straight line.</summary>
        Linear,
        /// <summary>Gets moving early, easing off near the top.</summary>
        Steep,
    }

    /// <summary>
    /// Builds a curve from a description: nothing below <paramref name="start"/> but
    /// <paramref name="min"/>, 100 % at <paramref name="full"/>, and the shape between. Five
    /// points, enough for smooth interpolation.
    /// </summary>
    public static IReadOnlyList<CurvePoint> Generate(double start, double full, double min, CurveShape shape)
    {
        min = Math.Clamp(min, 0, 100);
        if (full <= start) full = start + 1;
        double Ease(double f) => shape switch
        {
            CurveShape.Gentle => f * f,
            CurveShape.Steep => Math.Sqrt(f),
            _ => f,
        };
        var points = new List<CurvePoint> { new(Math.Round(start), Math.Round(min)) };
        for (int i = 1; i <= 3; i++)
        {
            double f = i / 4.0;
            points.Add(new CurvePoint(Math.Round(start + (full - start) * f), Math.Round(min + (100 - min) * Ease(f))));
        }
        points.Add(new CurvePoint(Math.Round(full), 100));
        return points;
    }
}

/// <summary>A curve shape the user named, so one build can be applied to any fan.</summary>
public sealed record SavedCurve(string Name, string SensorKey, IReadOnlyList<CurvePoint> Points);

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

    /// <summary>
    /// How long a curve's lower target must hold before the fan is slowed to it. Stops the
    /// ramp-flatten-ramp noise when a temperature hovers around a bend in the curve; a deep
    /// enough drop skips the wait. Rising speeds are always immediate.
    /// </summary>
    public TimeSpan Hysteresis { get; set; } = TimeSpan.FromSeconds(8);
    public const double HysteresisDeepDrop = 15;

    private readonly Dictionary<string, (double Percent, DateTime At)> _driven = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _coolingSince = new(StringComparer.OrdinalIgnoreCase);

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

    public void Apply(IReadOnlyList<IFanChannel> fans, IReadOnlyDictionary<string, FanSetting> settings, Func<string, double?> read) =>
        Apply(fans, settings, read, DateTime.UtcNow);

    public void Apply(IReadOnlyList<IFanChannel> fans, IReadOnlyDictionary<string, FanSetting> settings,
        Func<string, double?> read, DateTime now)
    {
        foreach (var fan in fans)
        {
            var target = Target(settings.GetValueOrDefault(fan.Id), read, fan.GuardKey);
            if (target is not { } percent)
            {
                if (_driven.Remove(fan.Id)) Try(fan.Reset);
                _coolingSince.Remove(fan.Id);
                continue;
            }

            // Hysteresis: when a curve asks for less, hold the current speed a moment before
            // following it down, so a temperature sitting on a bend doesn't saw the fan up and down.
            if (_driven.TryGetValue(fan.Id, out var last) && percent < last.Percent - 0.5)
            {
                var since = _coolingSince.TryGetValue(fan.Id, out var held) ? held : now;
                if (now - since < Hysteresis && last.Percent - percent < HysteresisDeepDrop)
                {
                    _coolingSince[fan.Id] = since;
                    continue;
                }
            }
            _coolingSince.Remove(fan.Id);

            // Resend now and then too: some drivers quietly take a fan back.
            if (_driven.TryGetValue(fan.Id, out var driven) && Math.Abs(driven.Percent - percent) < 1 && now - driven.At < Refresh) continue;
            Try(() => fan.Set(percent));
            _driven[fan.Id] = (percent, now);
        }
    }

    public void ResetAll(IReadOnlyList<IFanChannel> fans)
    {
        foreach (var fan in fans.Where(f => _driven.ContainsKey(f.Id))) Try(fan.Reset);
        _driven.Clear();
        _coolingSince.Clear();
    }

    private static void Try(Action action)
    {
        try { action(); }
        catch (Exception) { /* a fan that won't take a command keeps its own control */ }
    }
}
