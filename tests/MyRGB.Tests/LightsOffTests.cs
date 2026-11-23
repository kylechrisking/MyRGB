using MyRGB.Core;
using MyRGB.Core.Devices;
using MyRGB.Core.Effects;
using MyRGB.Core.Engine;
using MyRGB.Core.Layout;
using MyRGB.Core.Presets;

namespace MyRGB.Tests;

public class LightsOffTests
{
    private static TimeOnly T(int h, int m = 0) => new(h, m);

    [Theory]
    [InlineData(23, 0, true)]
    [InlineData(2, 30, true)]
    [InlineData(6, 59, true)]
    [InlineData(7, 0, false)]
    [InlineData(12, 0, false)]
    [InlineData(22, 59, false)]
    public void Night_hours_run_past_midnight(int h, int m, bool inside) =>
        Assert.Equal(inside, new NightHours(T(23), T(7)).Contains(T(h, m)));

    [Fact]
    public void Night_hours_within_one_day_and_empty()
    {
        var afternoon = new NightHours(T(13), T(15));
        Assert.True(afternoon.Contains(T(14)));
        Assert.False(afternoon.Contains(T(15)));
        Assert.False(afternoon.Contains(T(12)));
        Assert.False(new NightHours(T(7), T(7)).Contains(T(7)));
    }

    [Fact]
    public void Night_hours_choices_and_parsing()
    {
        Assert.Equal(48, NightHours.Choices.Count);
        Assert.Equal("00:00", NightHours.Choices[0]);
        Assert.Equal("23:30", NightHours.Choices[^1]);
        Assert.Equal(T(22, 30), NightHours.Parse("22:30", T(1)));
        Assert.Equal(T(1), NightHours.Parse("late", T(1)));
    }

    private static (LightingService Svc, LightingEngine Engine) Build()
    {
        var effects = EffectRegistry.CreateDefault();
        var devices = new DeviceManager();
        var engine = new LightingEngine(devices, effects, new CanvasLayout());
        return (new LightingService(engine, new PresetLibrary(), effects, devices), engine);
    }

    [Fact]
    public void A_hold_turns_the_lights_off_without_changing_the_saved_choice()
    {
        var (svc, engine) = Build();
        using var _ = engine;
        svc.Hold("night", "night hours, until 07:00");
        Assert.False(svc.State.Enabled);
        Assert.Equal("night hours, until 07:00", svc.State.HeldBy);
        Assert.True(engine.Enabled); // what gets saved
        Assert.True(engine.HeldOff);

        svc.Release("night");
        Assert.True(svc.State.Enabled);
        Assert.Null(svc.State.HeldBy);
        Assert.False(engine.HeldOff);
    }

    [Fact]
    public void Holds_stack_and_the_latest_reason_shows()
    {
        var (svc, engine) = Build();
        using var _ = engine;
        svc.Hold("night", "night hours, until 07:00");
        svc.Hold("locked", "Windows is locked");
        Assert.Equal("Windows is locked", svc.State.HeldBy);
        svc.Release("locked");
        Assert.Equal("night hours, until 07:00", svc.State.HeldBy);
        Assert.True(engine.HeldOff);
    }

    [Fact]
    public void Turning_the_lights_on_ends_every_hold()
    {
        var (svc, engine) = Build();
        using var _ = engine;
        svc.Hold("night", "night hours, until 07:00");
        svc.Hold("locked", "Windows is locked");
        svc.SetEnabled(!svc.State.Enabled); // what the power button, tray, hotkey and Stream Deck do
        Assert.True(svc.State.Enabled);
        Assert.False(svc.IsHeld("night"));
        Assert.False(engine.HeldOff);
    }

    [Fact]
    public void Switched_off_lights_report_no_hold()
    {
        var (svc, engine) = Build();
        using var _ = engine;
        svc.SetEnabled(false);
        svc.Hold("locked", "Windows is locked");
        Assert.Null(svc.State.HeldBy); // they're off because you turned them off
        svc.Release("locked");
        Assert.False(svc.State.Enabled);
    }

}
