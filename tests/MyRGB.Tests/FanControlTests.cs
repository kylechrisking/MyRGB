using MyRGB.Core.Sensors;

namespace MyRGB.Tests;

public class FanControlTests
{
    [Fact]
    public void A_curve_follows_the_hottest_of_its_temperatures()
    {
        var fan = new SpinningFan("case1");
        var settings = Settings(new FanSetting
        {
            FanId = "case1", Mode = FanMode.Curve, SensorKey = SensorKeys.CpuTemp,
            AlsoFollow = [SensorKeys.GpuTemp],
        });
        double? Read(string key) => key switch { SensorKeys.CpuTemp => 50, SensorKeys.GpuTemp => 65, _ => null };

        new FanController().Apply([fan], settings, Read);

        Assert.Equal([60.0], fan.Sets); // the GPU's 65 °C, not the CPU's 50
    }

    [Fact]
    public void A_curve_skips_temperatures_it_cannot_read()
    {
        var fan = new SpinningFan("case1");
        var settings = Settings(new FanSetting
        {
            FanId = "case1", Mode = FanMode.Curve, SensorKey = SensorKeys.CpuTemp,
            AlsoFollow = [SensorKeys.GpuTemp],
        });

        new FanController().Apply([fan], settings, key => key == SensorKeys.GpuTemp ? 65 : null);

        Assert.Equal([60.0], fan.Sets);
    }

    [Fact]
    public void A_matched_fan_copies_the_other_fans_speed_plus_its_offset()
    {
        var leader = new SpinningFan("front");
        var follower = new SpinningFan("rear");
        var settings = new Dictionary<string, FanSetting>(StringComparer.OrdinalIgnoreCase)
        {
            // Listed follower-first on purpose: order in the list mustn't matter.
            ["rear"] = new() { FanId = "rear", Mode = FanMode.Match, MatchFanId = "front", MatchOffset = -10 },
            ["front"] = new() { FanId = "front", Mode = FanMode.Fixed, FixedPercent = 70 },
        };

        new FanController().Apply([follower, leader], settings, _ => 40);

        Assert.Equal([70.0], leader.Sets);
        Assert.Equal([60.0], follower.Sets);
    }

    [Fact]
    public void Fans_that_copy_each_other_in_a_loop_are_left_alone()
    {
        var a = new SpinningFan("a");
        var b = new SpinningFan("b");
        var settings = new Dictionary<string, FanSetting>(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = new() { FanId = "a", Mode = FanMode.Match, MatchFanId = "b" },
            ["b"] = new() { FanId = "b", Mode = FanMode.Match, MatchFanId = "a" },
        };

        new FanController().Apply([a, b], settings, _ => 40);

        Assert.Empty(a.Sets);
        Assert.Empty(b.Sets);
    }

    [Fact]
    public void A_speed_too_low_to_spin_is_raised_to_the_minimum_but_zero_stays_zero()
    {
        var setting = new FanSetting { FanId = "f", Mode = FanMode.Fixed, FixedPercent = 10, MinSpinPercent = 25 };
        Assert.Equal(25, FanController.Target(setting, _ => 40, SensorKeys.CpuTemp));
        Assert.Equal(0, FanController.Target(setting with { FixedPercent = 0 }, _ => 40, SensorKeys.CpuTemp));
        Assert.Equal(40, FanController.Target(setting with { FixedPercent = 40 }, _ => 40, SensorKeys.CpuTemp));
    }

    [Fact]
    public void A_test_finds_where_the_fan_stops_and_starts()
    {
        // Spins at 1500 RPM flat out; stops below 20 %, needs 30 % to start again.
        var fan = new SpinningFan("case1") { StopBelow = 20, StartAt = 30 };
        var test = FanCalibrator.TryStart(fan, out var whyNot)!;
        Assert.Null(whyNot);

        Run(test, fan, _ => 40);

        Assert.Null(test.Failure);
        var result = test.Result!;
        Assert.Equal(1500, result.MaxRpm);
        Assert.Equal(20, result.StopPercent);
        Assert.Equal(30, result.StartPercent);
    }

    [Fact]
    public void A_fan_that_never_stops_reports_no_stop_speed()
    {
        var fan = new SpinningFan("gpu") { StopBelow = 0, StartAt = 0 };
        var test = FanCalibrator.TryStart(fan, out _)!;

        Run(test, fan, _ => 40);

        Assert.Equal(0, test.Result!.StopPercent);
        Assert.Equal(0, test.Result.StartPercent);
    }

    [Fact]
    public void A_test_ends_early_if_it_gets_hot()
    {
        var fan = new SpinningFan("case1") { StopBelow = 20, StartAt = 30 };
        var test = FanCalibrator.TryStart(fan, out _)!;
        double temp = 50;

        var now = DateTime.UtcNow;
        for (int i = 0; i < 20; i++) test.Step(_ => temp, now = now.AddSeconds(1));
        temp = 80;
        test.Step(_ => temp, now.AddSeconds(1));

        Assert.True(test.Finished);
        Assert.Null(test.Result);
        Assert.NotNull(test.Failure);
    }

    [Fact]
    public void Pumps_and_fans_without_an_rpm_are_not_tested()
    {
        Assert.Null(FanCalibrator.TryStart(new SpinningFan("p") { Label = "Pump" }, out var pump));
        Assert.NotNull(pump);
        Assert.Null(FanCalibrator.TryStart(new SpinningFan("q") { ReportsRpm = false }, out var blind));
        Assert.NotNull(blind);
    }

    [Fact]
    public void The_hub_sets_a_fan_aside_while_testing_it_and_hands_it_back_after()
    {
        var fan = new SpinningFan("case1") { StopBelow = 20, StartAt = 30 };
        var source = new OneFan(fan);
        using var hub = new SensorHub(source);
        hub.SetFanSettings([new FanSetting { FanId = "case1", Mode = FanMode.Fixed, FixedPercent = 45 }]);
        FanCalibrator? ended = null;
        hub.CalibrationEnded += t => ended = t;

        Assert.Null(hub.StartCalibration("case1"));
        hub.Poll();
        Assert.Equal(100, fan.Sets[^1]); // the test, not the fixed 45 %

        hub.CancelCalibration("case1");
        hub.Poll();

        Assert.NotNull(ended);
        Assert.True(hub.Calibration("case1")!.Finished);
        Assert.Equal(45, fan.Sets[^1]); // its own setting again
    }

    /// <summary>Steps a test to the end, as the sensor thread would, one second a step.</summary>
    private static void Run(FanCalibrator test, SpinningFan fan, Func<string, double?> read)
    {
        var now = DateTime.UtcNow;
        for (int i = 0; i < 1000 && !test.Finished; i++)
        {
            now = now.AddSeconds(1);
            test.Step(read, now);
        }
        Assert.True(test.Finished);
    }

    private static Dictionary<string, FanSetting> Settings(FanSetting s) =>
        new(StringComparer.OrdinalIgnoreCase) { [s.FanId] = s };

    /// <summary>A fan whose RPM follows the speed it was given, with a stall zone like a real one.</summary>
    private sealed class SpinningFan(string id) : IFanChannel
    {
        public double StopBelow { get; init; }
        public double StartAt { get; init; }
        public string Label { get; init; } = "Fan";
        public bool ReportsRpm { get; init; } = true;
        public List<double> Sets { get; } = [];
        private double _percent = 50;
        private bool _spinning = true;

        public string Id => id;
        public string Name => Label;
        public string GuardKey => SensorKeys.CpuTemp;
        public double? Rpm => ReportsRpm ? (_spinning ? 300 + _percent * 12 : 0) : null;
        public double? Percent => _percent;

        public void Set(double percent)
        {
            Sets.Add(percent);
            _percent = percent;
            if (_spinning && percent < StopBelow) _spinning = false;
            else if (!_spinning && percent >= StartAt) _spinning = true;
        }

        public void Reset() => Sets.Add(-1);
    }

    private sealed class OneFan(IFanChannel fan) : ISensorSource
    {
        public IReadOnlyList<SensorReading> Read() =>
            [new("cpu/t", SensorCategory.Cpu, "CPU", "Package", SensorKind.Temperature, 40, SensorKeys.CpuTemp)];
        public IReadOnlyList<IFanChannel> Fans => [fan];
        public void Dispose() { }
    }
}
