using MyRGB.Core.Engine;

namespace MyRGB.Tests;

public class FadeTests
{
    private static void Run(Fade f, bool on, double brightness, double seconds)
    {
        for (double t = 0; t < seconds - 1e-9; t += 1 / 30.0) f.Step(on, brightness, 1 / 30.0);
    }

    [Fact]
    public void Switching_off_glides_to_black_then_stops_lighting()
    {
        var f = new Fade();
        Run(f, on: false, 1, Fade.PowerSeconds / 2);
        Assert.True(f.Lit);
        Assert.InRange(f.Level, 0.2, 0.3); // half way in time, a quarter in light: even to the eye
        Run(f, on: false, 1, Fade.PowerSeconds);
        Assert.False(f.Lit);
        Assert.Equal(0, f.Level);
    }

    [Fact]
    public void Switching_on_glides_up_to_the_chosen_brightness()
    {
        var f = new Fade { SnapNext = true };
        f.Step(false, 0.5, 0.03);
        Assert.False(f.Lit);
        Run(f, on: true, 0.5, Fade.PowerSeconds + 0.1);
        Assert.Equal(0.5, f.Level, 6);
    }

    [Fact]
    public void Brightness_changes_ease_quickly()
    {
        var f = new Fade();
        f.Step(true, 0.25, 1 / 30.0);
        Assert.True(f.Level > 0.8); // not a jump
        Run(f, true, 0.25, Fade.BrightnessSeconds);
        Assert.Equal(0.25, f.Level, 6);
    }

    [Fact]
    public void Snap_skips_the_fade_once()
    {
        var f = new Fade { SnapNext = true };
        f.Step(false, 1, 0.001);
        Assert.False(f.Lit);
        Assert.False(f.SnapNext);
        f.Step(true, 1, 1 / 30.0);
        Assert.True(f.Level < 0.1); // fading in again, not snapping
    }
}
