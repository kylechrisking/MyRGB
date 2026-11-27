namespace MyRGB.Core.Sensors;

public enum FanMode
{
    Auto,
    Fixed,
    Curve,
    /// <summary>Runs at whatever speed another fan is running at, plus an offset.</summary>
    Match,
}

public readonly record struct CurvePoint(double Temp, double Percent);

/// <summary>How the user wants one fan driven. Saved in settings.</summary>
public sealed record FanSetting
{
    public required string FanId { get; init; }
    public FanMode Mode { get; init; } = FanMode.Auto;
    public double FixedPercent { get; init; } = 50;
    public string SensorKey { get; init; } = SensorKeys.GpuTemp;
    public List<CurvePoint> Points { get; init; } = [.. FanCurve.Balanced];

    /// <summary>
    /// More temperatures a curve watches besides <see cref="SensorKey"/>; it follows whichever is
    /// hottest. Ones that can't be read are skipped.
    /// </summary>
    public List<string> AlsoFollow { get; init; } = [];

    /// <summary>For <see cref="FanMode.Match"/>: the fan this one copies.</summary>
    public string? MatchFanId { get; init; }

    /// <summary>For <see cref="FanMode.Match"/>: added to the other fan's speed, in points (may be negative).</summary>
    public double MatchOffset { get; init; }

    /// <summary>
    /// The lowest speed this fan reliably spins at. A speed above 0 but below it is raised to it,
    /// so the fan never sits stalled at a speed too low to turn. 0 means no minimum.
    /// </summary>
    public double MinSpinPercent { get; init; }

    /// <summary>Every temperature the curve watches, its main one first.</summary>
    public IEnumerable<string> FollowKeys() => AlsoFollow.Prepend(SensorKey).Distinct(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>Ids of fans being tested by <see cref="FanCalibrator"/>; left alone until the test ends.</summary>
    public ISet<string> Paused { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The hottest of the temperatures a curve watches, or null when none can be read.</summary>
    public static double? Hottest(FanSetting setting, Func<string, double?> read)
    {
        double? hottest = null;
        foreach (var key in setting.FollowKeys())
            if (read(key) is { } t && (hottest is null || t > hottest)) hottest = t;
        return hottest;
    }

    /// <summary>
    /// The speed a fan should run at, or null to leave it to the hardware. A fan on
    /// <see cref="FanMode.Match"/> needs <paramref name="other"/>: the speed of the fan it copies
    /// (null when that fan is on its own control and doesn't report a speed).
    /// </summary>
    public static double? Target(FanSetting? setting, Func<string, double?> read, string guardKey, Func<string, double?>? other = null)
    {
        if (setting is null || setting.Mode == FanMode.Auto) return null;
        if (read(guardKey) is >= EmergencyTemp) return 100;

        double wanted;
        if (setting.Mode == FanMode.Fixed) wanted = setting.FixedPercent;
        else if (setting.Mode == FanMode.Match)
        {
            if (setting.MatchFanId is not { } leader || other?.Invoke(leader) is not { } speed) return null;
            wanted = speed + setting.MatchOffset;
        }
        else if (Hottest(setting, read) is { } t)
        {
            if (t >= EmergencyTemp) return 100;
            wanted = FanCurve.Evaluate(setting.Points, t);
        }
        else return null; // can't see the temperature: let the hardware decide
        wanted = Math.Clamp(wanted, Floor, 100);
        if (wanted > 0 && wanted < setting.MinSpinPercent) wanted = Math.Min(setting.MinSpinPercent, 100);
        return wanted;
    }

    public void Apply(IReadOnlyList<IFanChannel> fans, IReadOnlyDictionary<string, FanSetting> settings, Func<string, double?> read) =>
        Apply(fans, settings, read, DateTime.UtcNow);

    public void Apply(IReadOnlyList<IFanChannel> fans, IReadOnlyDictionary<string, FanSetting> settings,
        Func<string, double?> read, DateTime now)
    {
        // Fans that copy another go after the ones they copy, so they see this round's speed.
        var byId = fans.ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);
        var ordered = fans.OrderBy(f => MatchDepth(f.Id, settings)).ToList();
        double? Other(string id)
        {
            if (_driven.TryGetValue(id, out var d)) return d.Percent;
            // The other fan is on its own control: copy what it reports, if it does.
            return byId.TryGetValue(id, out var f) && settings.GetValueOrDefault(id) is not { Mode: not FanMode.Auto } ? f.Percent : null;
        }

        foreach (var fan in ordered)
        {
            if (Paused.Contains(fan.Id)) continue;
            var target = Target(settings.GetValueOrDefault(fan.Id), read, fan.GuardKey, Other);
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

    /// <summary>How many fans down a chain of copies this one sits; 0 for a fan that copies nothing. Loops count as 0.</summary>
    private static int MatchDepth(string id, IReadOnlyDictionary<string, FanSetting> settings)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int depth = 0;
        while (settings.GetValueOrDefault(id) is { Mode: FanMode.Match, MatchFanId: { } next } && seen.Add(id))
        {
            id = next;
            depth++;
        }
        return seen.Contains(id) ? 0 : depth;
    }

    /// <summary>Forget what was last sent to a fan, so the next round sends again (after a test, say).</summary>
    public void Forget(string fanId)
    {
        _driven.Remove(fanId);
        _coolingSince.Remove(fanId);
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
