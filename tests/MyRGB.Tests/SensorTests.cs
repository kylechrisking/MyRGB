using MyRGB.Core.Sensors;

namespace MyRGB.Tests;

public class SensorTests
{
    [Theory]
    [InlineData(20, 30)]
    [InlineData(50, 40)]
    [InlineData(57.5, 50)]
    [InlineData(80, 100)]
    [InlineData(95, 100)]
    public void Curve_interpolates_between_points(double temp, double percent) =>
        Assert.Equal(percent, FanCurve.Evaluate(FanCurve.Balanced, temp), 3);

    [Fact]
    public void Auto_fans_are_never_touched()
    {
        var fan = new FakeFan("gpu1");
        new FanController().Apply([fan], new Dictionary<string, FanSetting>(), _ => 60);
        Assert.Empty(fan.Commands);
    }

    [Fact]
    public void Fixed_speed_is_held_above_the_floor()
    {
        var fan = new FakeFan("gpu1");
        var settings = Settings(new FanSetting { FanId = "gpu1", Mode = FanMode.Fixed, FixedPercent = 5 });
        new FanController().Apply([fan], settings, _ => 40);
        Assert.Equal(["set 30"], fan.Commands);
    }

    [Fact]
    public void Overheating_runs_the_fan_flat_out_whatever_the_setting()
    {
        var fan = new FakeFan("gpu1");
        var settings = Settings(new FanSetting { FanId = "gpu1", Mode = FanMode.Fixed, FixedPercent = 40 });
        new FanController().Apply([fan], settings, key => key == SensorKeys.GpuTemp ? 91 : null);
        Assert.Equal(["set 100"], fan.Commands);
    }

    [Fact]
    public void A_curve_that_loses_its_sensor_hands_the_fan_back()
    {
        var fan = new FakeFan("gpu1");
        var controller = new FanController();
        var settings = Settings(new FanSetting { FanId = "gpu1", Mode = FanMode.Curve, SensorKey = SensorKeys.GpuTemp });

        controller.Apply([fan], settings, _ => 65);
        controller.Apply([fan], settings, _ => null);
        Assert.Equal(["set 60", "reset"], fan.Commands);
    }

    [Fact]
    public void Switching_back_to_auto_and_exiting_reset_only_driven_fans()
    {
        var driven = new FakeFan("gpu1");
        var untouched = new FakeFan("gpu2");
        var controller = new FanController();
        controller.Apply([driven, untouched], Settings(new FanSetting { FanId = "gpu1", Mode = FanMode.Fixed, FixedPercent = 70 }), _ => 40);
        controller.ResetAll([driven, untouched]);

        Assert.Equal(["set 70", "reset"], driven.Commands);
        Assert.Empty(untouched.Commands);
    }

    [Fact]
    public void Hub_keeps_history_and_named_values()
    {
        var source = new StepSource();
        using var hub = new SensorHub(source);
        for (int i = 0; i < 3; i++) hub.Poll();

        Assert.Equal(2, hub.Get(SensorKeys.GpuTemp));
        Assert.Equal([0f, 1f, 2f], hub.History(SensorKeys.GpuTemp));
    }

    [Fact]
    public void System_heat_turns_hot_as_the_sensor_climbs()
    {
        var def = MyRGB.Core.Effects.EffectRegistry.CreateDefault().Find("sensor-meter")!;
        var effect = def.Create(MyRGB.Core.Effects.EffectSettings.Empty);
        var leds = new MyRGB.Core.Effects.LedPoint[] { new(0.5f, 0.5f) };
        var output = new MyRGB.Core.Rgb[1];
        try
        {
            SensorFeed.Use(_ => 80);
            effect.Render(new MyRGB.Core.Effects.FrameContext(0, 0), leds, output);
            Assert.Equal(new MyRGB.Core.Rgb(255, 30, 0), output[0]);

            SensorFeed.Use(_ => null); // lost the reading: eases back to the cool end
            for (int i = 0; i < 60; i++) effect.Render(new MyRGB.Core.Effects.FrameContext(i, 1), leds, output);
            Assert.Equal(new MyRGB.Core.Rgb(0, 200, 255), output[0]);
        }
        finally { SensorFeed.Use(_ => null); }
    }

    [Fact]
    public void Cool_to_hot_passes_through_yellow_not_purple()
    {
        var mid = MyRGB.Core.Rgb.LerpHue(new(0, 200, 255), new(255, 30, 0), 0.5);
        var (h, _, _) = mid.ToHsv();
        Assert.InRange(h, 60, 130); // green to yellow, not blue or magenta
    }

    private static Dictionary<string, FanSetting> Settings(FanSetting s) => new() { [s.FanId] = s };

    private sealed class FakeFan(string id) : IFanChannel
    {
        public List<string> Commands { get; } = [];
        public string Id => id;
        public string Name => id;
        public string GuardKey => SensorKeys.GpuTemp;
        public double? Rpm => 1000;
        public double? Percent => 40;
        public void Set(double percent) => Commands.Add($"set {percent:0}");
        public void Reset() => Commands.Add("reset");
    }

    private sealed class StepSource : ISensorSource
    {
        private int _n;
        public IReadOnlyList<SensorReading> Read() =>
            [new("gpu/temp", SensorCategory.Gpu, "GPU", "GPU Core", SensorKind.Temperature, _n++, SensorKeys.GpuTemp)];
        public IReadOnlyList<IFanChannel> Fans => [];
        public void Dispose() { }
    }
}
