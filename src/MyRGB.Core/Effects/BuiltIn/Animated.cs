namespace MyRGB.Core.Effects.BuiltIn;

internal static class Noise
{
    /// <summary>Smooth value noise in 3D, range 0..1. Deterministic, allocation-free.</summary>
    public static double Value(double x, double y, double z)
    {
        int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y), zi = (int)Math.Floor(z);
        double xf = Fade(x - xi), yf = Fade(y - yi), zf = Fade(z - zi);

        double Corner(int dx, int dy, int dz) => Hash(xi + dx, yi + dy, zi + dz);
        double Lerp(double a, double b, double t) => a + (b - a) * t;

        double x00 = Lerp(Corner(0, 0, 0), Corner(1, 0, 0), xf);
        double x10 = Lerp(Corner(0, 1, 0), Corner(1, 1, 0), xf);
        double x01 = Lerp(Corner(0, 0, 1), Corner(1, 0, 1), xf);
        double x11 = Lerp(Corner(0, 1, 1), Corner(1, 1, 1), xf);
        return Lerp(Lerp(x00, x10, yf), Lerp(x01, x11, yf), zf);
    }

    private static double Fade(double t) => t * t * (3 - 2 * t);

    private static double Hash(int x, int y, int z)
    {
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + z * 1274126177);
            h = (h ^ (h >> 13)) * 1274126177;
            return ((h ^ (h >> 16)) & 0xFFFFFF) / (double)0xFFFFFF;
        }
    }
}

internal static class Palettes
{
    public static readonly string[] Names = ["Rainbow", "Ocean", "Lava", "Forest", "Neon", "Sunset"];

    private static readonly Dictionary<string, Rgb[]> Stops = new()
    {
        ["Ocean"] = [new(0, 20, 80), new(0, 120, 255), new(0, 255, 220), new(0, 60, 160)],
        ["Lava"] = [new(60, 0, 0), new(255, 30, 0), new(255, 140, 0), new(120, 0, 0)],
        ["Forest"] = [new(0, 60, 10), new(40, 200, 30), new(180, 255, 60), new(0, 100, 40)],
        ["Neon"] = [new(255, 0, 200), new(120, 0, 255), new(0, 220, 255), new(255, 0, 90)],
        ["Sunset"] = [new(255, 60, 0), new(255, 0, 90), new(120, 0, 160), new(255, 150, 0)],
    };

    /// <summary>Sample a looping palette at t (wraps every 1.0).</summary>
    public static Rgb Sample(string palette, double t)
    {
        t = ((t % 1) + 1) % 1;
        if (!Stops.TryGetValue(palette, out var stops)) return Rgb.FromHsv(t * 360, 1, 1);
        double scaled = t * stops.Length;
        int i = (int)scaled;
        return Rgb.Lerp(stops[i % stops.Length], stops[(i + 1) % stops.Length], scaled - i);
    }
}

/// <summary>Effects that keep per-LED state need to reset it when the LED set changes.</summary>
internal abstract class StatefulEffect : Effect
{
    protected readonly Random Rng = new();

    protected static T[] Ensure<T>(ref T[] state, int length) where T : struct
    {
        if (state.Length != length) state = new T[length];
        return state;
    }
}

internal sealed class TwinkleEffect : StatefulEffect
{
    public static readonly EffectDefinition Definition = new(
        "twinkle", "Starfield", "LEDs randomly sparkle and fade, like stars.", "Animated",
        [
            new ColorParameter("background", "Background", new Rgb(0, 0, 12)),
            new ColorParameter("color", "Star color", new Rgb(255, 255, 255)),
            new BoolParameter("random", "Random star colors", false),
            new NumberParameter("density", "Density", 1, 10, 4),
            new NumberParameter("fade", "Fade speed", 1, 10, 4),
        ],
        () => new TwinkleEffect());

    private double[] _level = [];
    private Rgb[] _starColor = [];

    public override void Render(in FrameContext frame, ReadOnlySpan<LedPoint> leds, Span<Rgb> output)
    {
        var s = Settings;
        Ensure(ref _level, leds.Length);
        Ensure(ref _starColor, leds.Length);
        Rgb bg = s.GetColor("background"), star = s.GetColor("color");
        bool random = s.GetBool("random");
        double spawnChance = s.GetNumber("density") * 0.15 * frame.DeltaTime;
        double decay = s.GetNumber("fade") * 0.35 * frame.DeltaTime;

        for (int i = 0; i < leds.Length; i++)
        {
            if (_level[i] <= 0 && Rng.NextDouble() < spawnChance)
            {
                _level[i] = 1;
                _starColor[i] = random ? Rgb.FromHsv(Rng.NextDouble() * 360, 0.8, 1) : star;
            }
            output[i] = Rgb.Lerp(bg, _starColor[i], _level[i] * _level[i]);
            _level[i] = Math.Max(0, _level[i] - decay);
        }
    }
}

internal sealed class RippleEffect : StatefulEffect
{
    public static readonly EffectDefinition Definition = new(
        "ripple", "Ripples", "Rings of light spreading out from random points.", "Animated",
        [
            new ColorParameter("color", "Ripple color", new Rgb(0, 180, 255)),
            new ColorParameter("background", "Background", new Rgb(0, 0, 0)),
            new BoolParameter("random", "Random colors", true),
            new NumberParameter("speed", "Speed", 1, 10, 4),
            new NumberParameter("rate", "Ripples per second", 0.2, 5, 1, 0.1),
        ],
        () => new RippleEffect());

    private readonly List<(float X, float Y, double Born, Rgb Color)> _ripples = [];
    private double _sinceSpawn;

    public override void Render(in FrameContext frame, ReadOnlySpan<LedPoint> leds, Span<Rgb> output)
    {
        var s = Settings;
        double speed = s.GetNumber("speed") * 0.12, ringWidth = 0.12;
        _sinceSpawn += frame.DeltaTime;
        if (_sinceSpawn >= 1 / Math.Max(0.05, s.GetNumber("rate")))
        {
            _sinceSpawn = 0;
            var color = s.GetBool("random") ? Rgb.FromHsv(Rng.NextDouble() * 360, 1, 1) : s.GetColor("color");
            _ripples.Add(((float)Rng.NextDouble(), (float)Rng.NextDouble(), frame.Time, color));
        }
        double now = frame.Time;
        _ripples.RemoveAll(r => (now - r.Born) * speed > 1.6);

        output.Fill(s.GetColor("background"));
        foreach (var r in _ripples)
        {
            double radius = (now - r.Born) * speed;
            double life = 1 - radius / 1.6;
            for (int i = 0; i < leds.Length; i++)
            {
                double dx = leds[i].X - r.X, dy = leds[i].Y - r.Y;
                double d = Math.Abs(Math.Sqrt(dx * dx + dy * dy) - radius);
                if (d < ringWidth)
                    output[i] = Rgb.Max(output[i], r.Color.Scale((1 - d / ringWidth) * life));
            }
        }
    }
}

internal sealed class PlasmaEffect : Effect
{
    public static readonly EffectDefinition Definition = new(
        "plasma", "Plasma", "Slowly swirling, organic color fields.", "Animated",
        [
            new ChoiceParameter("palette", "Palette", Palettes.Names, "Neon"),
            new NumberParameter("speed", "Speed", 1, 10, 3),
            new NumberParameter("scale", "Detail", 1, 10, 3),
        ],
        () => new PlasmaEffect());

    public override void Render(in FrameContext frame, ReadOnlySpan<LedPoint> leds, Span<Rgb> output)
    {
        var s = Settings;
        string palette = s.GetString("palette");
        double t = frame.Time * s.GetNumber("speed") * 0.15, k = s.GetNumber("scale") * 2;
        for (int i = 0; i < leds.Length; i++)
        {
            double x = leds[i].X * k, y = leds[i].Y * k;
            double v = Math.Sin(x + t) + Math.Sin((y + t) / 2) + Math.Sin((x + y + t) / 2)
                       + Math.Sin(Math.Sqrt(x * x + y * y + 1) + t);
            output[i] = Palettes.Sample(palette, v / 8 + 0.5);
        }
    }
}

internal sealed class FireEffect : Effect
{
    public static readonly EffectDefinition Definition = new(
        "fire", "Fire", "Flickering flames rising from the bottom of the canvas.", "Animated",
        [
            new ColorParameter("hot", "Flame tip", new Rgb(255, 200, 40)),
            new ColorParameter("cool", "Flame base", new Rgb(255, 20, 0)),
            new NumberParameter("speed", "Speed", 1, 10, 5),
            new NumberParameter("height", "Flame height %", 20, 100, 70),
        ],
        () => new FireEffect());

    public override void Render(in FrameContext frame, ReadOnlySpan<LedPoint> leds, Span<Rgb> output)
    {
        var s = Settings;
        Rgb hot = s.GetColor("hot"), cool = s.GetColor("cool");
        double t = frame.Time * s.GetNumber("speed") * 0.4, height = s.GetNumber("height") / 100;
        for (int i = 0; i < leds.Length; i++)
        {
            var p = leds[i];
            double rise = 1 - p.Y; // 0 at the bottom of the canvas
            double flicker = Noise.Value(p.X * 6, rise * 4 - t * 2, t * 0.5);
            double heat = Math.Clamp(1 - rise / height + (flicker - 0.5) * 0.9, 0, 1);
            output[i] = Rgb.Lerp(cool, hot, heat * heat).Scale(Math.Min(1, heat * 1.6));
        }
    }
}
