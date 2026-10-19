namespace MyRGB.Core.Effects.BuiltIn;

internal static class Directions
{
    public static readonly string[] Names = ["Right", "Left", "Down", "Up", "Diagonal", "Outward", "Inward"];

    /// <summary>
    /// Distance of a point along the travel direction, 0..~1.4. Increasing this over time with a phase
    /// makes a pattern move that way.
    /// </summary>
    public static double Project(string direction, LedPoint p) => direction switch
    {
        "Left" => p.X,
        "Down" => 1 - p.Y,
        "Up" => p.Y,
        "Diagonal" => (1 - p.X + 1 - p.Y) / 2,
        "Outward" => 1 - Distance(p),
        "Inward" => Distance(p),
        _ => 1 - p.X, // Right
    };

    private static double Distance(LedPoint p)
    {
        double dx = p.X - 0.5, dy = p.Y - 0.5;
        return Math.Sqrt(dx * dx + dy * dy) * 1.41421356;
    }
}

internal sealed class StaticEffect : Effect
{
    public static readonly EffectDefinition Definition = new(
        "static", "Solid Color", "One color on every LED.", "Basic",
        [new ColorParameter("color", "Color", new Rgb(0, 140, 255))],
        () => new StaticEffect());

    public override void Render(in FrameContext frame, ReadOnlySpan<LedPoint> leds, Span<Rgb> output) =>
        output.Fill(Settings.GetColor("color"));
}

internal sealed class BreathingEffect : Effect
{
    public static readonly EffectDefinition Definition = new(
        "breathing", "Breathing", "A color that slowly fades in and out.", "Basic",
        [
            new ColorParameter("color", "Color", new Rgb(255, 0, 80)),
            new NumberParameter("speed", "Speed", 1, 10, 3),
            new NumberParameter("floor", "Lowest brightness %", 0, 80, 5),
        ],
        () => new BreathingEffect());

    public override void Render(in FrameContext frame, ReadOnlySpan<LedPoint> leds, Span<Rgb> output)
    {
        var s = Settings;
        double floor = s.GetNumber("floor") / 100;
        double hz = s.GetNumber("speed") * 0.08;
        // Squared sine reads as a smoother "breath" than a plain sine.
        double wave = Math.Pow((Math.Sin(frame.Time * hz * Math.Tau) + 1) / 2, 2);
        output.Fill(s.GetColor("color").Scale(floor + (1 - floor) * wave));
    }
}

internal sealed class SpectrumCycleEffect : Effect
{
    public static readonly EffectDefinition Definition = new(
        "spectrum", "Spectrum Cycle", "Every LED shifts through the rainbow together.", "Rainbow",
        [
            new NumberParameter("speed", "Speed", 1, 10, 3),
            new NumberParameter("saturation", "Saturation %", 0, 100, 100),
        ],
        () => new SpectrumCycleEffect());

    public override void Render(in FrameContext frame, ReadOnlySpan<LedPoint> leds, Span<Rgb> output)
    {
        var s = Settings;
        double hue = frame.Time * s.GetNumber("speed") * 12;
        output.Fill(Rgb.FromHsv(hue, s.GetNumber("saturation") / 100, 1));
    }
}

internal sealed class RainbowWaveEffect : Effect
{
    public static readonly EffectDefinition Definition = new(
        "rainbow-wave", "Rainbow Wave", "A rainbow that sweeps across all your devices.", "Rainbow",
        [
            new NumberParameter("speed", "Speed", 1, 10, 4),
            new ChoiceParameter("direction", "Direction", Directions.Names, "Right"),
            new NumberParameter("scale", "Rainbows across canvas", 0.25, 4, 1, 0.25),
            new NumberParameter("saturation", "Saturation %", 0, 100, 100),
        ],
        () => new RainbowWaveEffect());

    public override void Render(in FrameContext frame, ReadOnlySpan<LedPoint> leds, Span<Rgb> output)
    {
        var s = Settings;
        string dir = s.GetString("direction");
        double scale = s.GetNumber("scale"), sat = s.GetNumber("saturation") / 100;
        double phase = frame.Time * s.GetNumber("speed") * 0.1;
        for (int i = 0; i < leds.Length; i++)
            output[i] = Rgb.FromHsv((Directions.Project(dir, leds[i]) * scale + phase) * 360, sat, 1);
    }
}

internal sealed class GradientEffect : Effect
{
    public static readonly EffectDefinition Definition = new(
        "gradient", "Gradient", "Blend between two colors across the canvas, optionally drifting.", "Basic",
        [
            new ColorParameter("from", "Start color", new Rgb(255, 0, 140)),
            new ColorParameter("to", "End color", new Rgb(0, 200, 255)),
            new ChoiceParameter("direction", "Direction", Directions.Names, "Right"),
            new NumberParameter("speed", "Drift speed (0 = still)", 0, 10, 2),
        ],
        () => new GradientEffect());

    public override void Render(in FrameContext frame, ReadOnlySpan<LedPoint> leds, Span<Rgb> output)
    {
        var s = Settings;
        Rgb a = s.GetColor("from"), b = s.GetColor("to");
        string dir = s.GetString("direction");
        double phase = frame.Time * s.GetNumber("speed") * 0.05;
        for (int i = 0; i < leds.Length; i++)
        {
            // Triangle wave so the drift loops without a hard seam.
            double t = (Directions.Project(dir, leds[i]) + phase) % 2;
            output[i] = Rgb.Lerp(a, b, t > 1 ? 2 - t : t);
        }
    }
}

internal sealed class ColorWaveEffect : Effect
{
    public static readonly EffectDefinition Definition = new(
        "color-wave", "Color Wave", "Bands of color rolling over a background.", "Waves",
        [
            new ColorParameter("color", "Wave color", new Rgb(0, 255, 160)),
            new ColorParameter("background", "Background", new Rgb(0, 10, 40)),
            new ChoiceParameter("direction", "Direction", Directions.Names, "Right"),
            new NumberParameter("speed", "Speed", 1, 10, 4),
            new NumberParameter("width", "Band width %", 5, 100, 30),
            new NumberParameter("count", "Bands", 1, 6, 2),
        ],
        () => new ColorWaveEffect());

    public override void Render(in FrameContext frame, ReadOnlySpan<LedPoint> leds, Span<Rgb> output)
    {
        var s = Settings;
        Rgb wave = s.GetColor("color"), bg = s.GetColor("background");
        string dir = s.GetString("direction");
        double count = s.GetNumber("count"), width = s.GetNumber("width") / 100;
        double phase = frame.Time * s.GetNumber("speed") * 0.12;
        for (int i = 0; i < leds.Length; i++)
        {
            double pos = (Directions.Project(dir, leds[i]) * count + phase) % 1;
            double d = Math.Abs(pos - 0.5) * 2; // 0 at the band center, 1 at its edge
            double k = d < width ? Math.Pow(Math.Cos(d / width * Math.PI / 2), 2) : 0;
            output[i] = Rgb.Lerp(bg, wave, k);
        }
    }
}
