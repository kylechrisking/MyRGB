namespace MyRGB.Core.Engine;

/// <summary>
/// Eases the light level the devices actually get toward what was asked for, so switching on or
/// off, a Lights out hold or a jump in brightness glides instead of snapping.
/// </summary>
public sealed class Fade
{
    /// <summary>How long a full on/off fade takes.</summary>
    public const double PowerSeconds = 0.6;

    /// <summary>How long a full-range brightness change takes; short, so dragging the slider still feels direct.</summary>
    public const double BrightnessSeconds = 0.25;

    /// <summary>0..1: how far on the lights are.</summary>
    public double Power { get; private set; } = 1;

    /// <summary>The brightness the devices are getting right now, 0..1.</summary>
    public double Brightness { get; private set; } = 1;

    /// <summary>Jump straight to the targets on the next step, e.g. just before the PC sleeps.</summary>
    public bool SnapNext { get; set; }

    /// <summary>Move toward the targets by <paramref name="dt"/> seconds of real time.</summary>
    public void Step(bool on, double brightness, double dt)
    {
        if (SnapNext)
        {
            SnapNext = false;
            Power = on ? 1 : 0;
            Brightness = brightness;
            return;
        }
        Power = Toward(Power, on ? 1 : 0, dt / PowerSeconds);
        Brightness = Toward(Brightness, brightness, dt / BrightnessSeconds);
    }

    /// <summary>Whether anything should light at all this frame.</summary>
    public bool Lit => Power > 0;

    /// <summary>
    /// The brightness to render with. Power is squared so the fade looks even to the eye, which is
    /// far more sensitive to changes near black than near full.
    /// </summary>
    public double Level => Brightness * Power * Power;

    private static double Toward(double from, double to, double maxStep) =>
        from < to ? Math.Min(to, from + maxStep) : Math.Max(to, from - maxStep);
}
