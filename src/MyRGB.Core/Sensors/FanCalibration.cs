namespace MyRGB.Core.Sensors;

/// <summary>What a fan test found. Saved in settings so it survives a restart.</summary>
public sealed record FanCalibration
{
    public required string FanId { get; init; }
    /// <summary>Its speed at 100 %.</summary>
    public double MaxRpm { get; init; }
    /// <summary>Below this the spinning fan stops (0 when it never stopped).</summary>
    public double StopPercent { get; init; }
    /// <summary>The lowest speed that gets a stopped fan turning again.</summary>
    public double StartPercent { get; init; }
    /// <summary>The RPM measured at each speed on the way down.</summary>
    public List<CurvePoint> Rpm { get; init; } = [];
    public DateTime TestedAt { get; init; }
}

/// <summary>
/// Tests one fan: full speed, then down in steps to find where it stops, then back up to find
/// where it starts again, reading the RPM at each step. Takes about two minutes. Driven one
/// second at a time from the sensor thread through <see cref="Step"/>.
///
/// Safety: ends at once if the fan's guard temperature reaches <see cref="AbortTemp"/>; refuses
/// pumps and fans that don't report an RPM. However it ends, the hub hands the fan back.
/// </summary>
public sealed class FanCalibrator
{
    /// <summary>Lower than <see cref="FanController.EmergencyTemp"/>: a stopped fan heats things up fast.</summary>
    public const double AbortTemp = 75;
    /// <summary>At or below this the fan counts as stopped. A guess; some fans report a little noise when still.</summary>
    public const double StoppedRpm = 100;

    // Fans take a few seconds to settle at a new speed, longer to spin up from full to stop.
    public TimeSpan SpinUpHold { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan StepHold { get; init; } = TimeSpan.FromSeconds(5);

    private static readonly double[] Down = [90, 80, 70, 60, 50, 40, 30, 25, 20, 15, 10, 5, 0];
    private const double UpStep = 5, UpLimit = 60;

    private enum Phase { SpinUp, Down, Up, Done }

    private readonly IFanChannel _fan;
    private Phase _phase = Phase.SpinUp;
    private int _index;
    private double _percent = 100;
    private DateTime _since;
    private double _maxRpm;
    private double _stop;
    private readonly List<CurvePoint> _rpm = [];

    private FanCalibrator(IFanChannel fan) => _fan = fan;

    /// <summary>Starts a test, or says why this fan can't be tested.</summary>
    public static FanCalibrator? TryStart(IFanChannel fan, out string? whyNot)
    {
        whyNot = null;
        if (fan.Name.Contains("pump", StringComparison.OrdinalIgnoreCase))
            whyNot = "Pumps aren't tested: stopping one, even briefly, isn't safe.";
        else if (fan.Rpm is null)
            whyNot = "This fan doesn't report its speed, so MyRGB can't measure it.";
        return whyNot is null ? new FanCalibrator(fan) : null;
    }

    public string FanId => _fan.Id;
    public bool Finished => _phase == Phase.Done;
    public FanCalibration? Result { get; private set; }
    /// <summary>Why the test ended early; null if it finished or is still going.</summary>
    public string? Failure { get; private set; }

    /// <summary>Roughly how far along it is, 0 to 1.</summary>
    public double Progress => _phase switch
    {
        Phase.SpinUp => 0,
        Phase.Down => 0.1 + 0.7 * _index / Down.Length,
        Phase.Up => 0.8 + 0.2 * Math.Min(1, _percent / UpLimit),
        _ => 1,
    };

    /// <summary>Plain words for the Cooling page.</summary>
    public string Status => _phase switch
    {
        Phase.SpinUp => "Testing: full speed first",
        Phase.Down => $"Testing: slowing down, now at {_percent:0} %",
        Phase.Up => $"Testing: finding where it starts, now at {_percent:0} %",
        _ => Failure ?? "Test finished",
    };

    /// <summary>Move the test on; call about once a second, after the sensors were read.</summary>
    public void Step(Func<string, double?> read, DateTime now)
    {
        if (_phase == Phase.Done) return;
        if (read(_fan.GuardKey) is >= AbortTemp)
        {
            Fail($"Stopped the test early: it got to {AbortTemp:0} °C. The fan is back on its normal control.");
            return;
        }
        if (_since == default)
        {
            _since = now;
            Try(() => _fan.Set(_percent));
            return;
        }
        var hold = _phase == Phase.SpinUp ? SpinUpHold : StepHold;
        if (now - _since < hold)
        {
            Try(() => _fan.Set(_percent)); // some drivers take a fan back; keep asking
            return;
        }

        double rpm = _fan.Rpm ?? 0;
        switch (_phase)
        {
            case Phase.SpinUp:
                if (rpm <= StoppedRpm)
                {
                    Fail("The fan didn't speed up at 100 %, so MyRGB can't control it. It may be plugged into a header set to a fixed speed in the BIOS.");
                    return;
                }
                _maxRpm = rpm;
                _rpm.Add(new CurvePoint(100, rpm));
                _phase = Phase.Down;
                _index = 0;
                Go(Down[0], now);
                break;

            case Phase.Down:
                _rpm.Add(new CurvePoint(_percent, rpm));
                if (rpm <= StoppedRpm)
                {
                    // It stopped somewhere between the last speed and this one.
                    _stop = _index == 0 ? Down[0] : Down[_index - 1];
                    _phase = Phase.Up;
                    Go(_percent + UpStep, now);
                }
                else if (++_index < Down.Length) Go(Down[_index], now);
                else Finish(stop: 0, start: 0); // never stopped, even at 0 %
                break;

            case Phase.Up:
                if (rpm > StoppedRpm) Finish(_stop, _percent);
                else if (_percent + UpStep > UpLimit) Finish(_stop, UpLimit); // didn't restart by 60 %: play it safe
                else Go(_percent + UpStep, now);
                break;
        }
    }

    /// <summary>Ends the test where it is (the user cancelled, or the app is closing).</summary>
    public void Cancel() => Fail("Test cancelled.");

    private void Go(double percent, DateTime now)
    {
        _percent = percent;
        _since = now;
        Try(() => _fan.Set(percent));
    }

    private void Finish(double stop, double start)
    {
        Result = new FanCalibration
        {
            FanId = _fan.Id,
            MaxRpm = Math.Round(_maxRpm),
            StopPercent = stop,
            StartPercent = start,
            Rpm = [.. _rpm.OrderBy(p => p.Temp)],
            TestedAt = DateTime.UtcNow,
        };
        _phase = Phase.Done;
    }

    private void Fail(string why)
    {
        Failure = why;
        _phase = Phase.Done;
    }

    private static void Try(Action action)
    {
        try { action(); }
        catch (Exception) { /* a fan that won't take a command keeps its own control */ }
    }

    /// <summary>One line on what a test found.</summary>
    public static string Describe(FanCalibration c) =>
        c.StopPercent <= 0
            ? $"Top speed {c.MaxRpm:0} RPM. It kept turning all the way down to 0 %."
            : $"Top speed {c.MaxRpm:0} RPM. Stops below {c.StopPercent:0} %, starts again at {c.StartPercent:0} %.";
}
