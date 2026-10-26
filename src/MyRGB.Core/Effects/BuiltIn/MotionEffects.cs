namespace MyRGB.Core.Effects.BuiltIn;

/// <summary>A bright head with a fading tail, streaking across the canvas device to device.</summary>
internal sealed class CometEffect : Effect
{
    public static readonly EffectDefinition Definition = new(
        "comet", "Comet", "A bright streak with a fading tail races across your desk.", "Animated",
        [
            new ColorParameter("color", "Color", new Rgb(0, 200, 255)),
            new BoolParameter("rainbow", "Rainbow tail", false),
            new ChoiceParameter("direction", "Direction", Directions.Names, "Right"),
            new NumberParameter("speed", "Speed", 1, 10, 4),
            new NumberParameter("tail", "Tail length", 5, 80, 35),
            new NumberParameter("count", "Comets", 1, 4, 1),
        ],
        () => new CometEffect());

    public override void Render(in FrameContext frame, ReadOnlySpan<LedPoint> leds, Span<Rgb> output)
    {
        var s = Settings;
        double speed = s.GetNumber("speed") * 0.09, tail = s.GetNumber("tail") / 100;
        int count = Math.Max(1, (int)s.GetNumber("count"));
        bool rainbow = s.GetBool("rainbow");
        string direction = s.GetString("direction");
        var color = s.GetColor("color");
        // Heads travel past the far edge by a tail's length, so tails leave cleanly before wrapping.
        double span = 1.4 + tail, head = frame.Time * speed % span;

        for (int i = 0; i < leds.Length; i++)
        {
            double along = 1 - Directions.Project(direction, leds[i]); // 0 at the edge it starts from
            double best = 0, bestBehind = 0;
            for (int c = 0; c < count; c++)
            {
                double h = (head + c * span / count) % span;
                double behind = h - along; // how far this LED trails the head
                if (behind < 0 || behind > tail) continue;
                double v = Math.Pow(1 - behind / tail, 2.2);
                if (v > best) { best = v; bestBehind = behind; }
            }
            var c2 = rainbow ? Rgb.FromHsv(frame.Time * 40 + bestBehind / tail * 240, 1, 1) : color;
            // The very head is a little whiter, like a real comet.
            output[i] = best > 0.92 ? Rgb.Lerp(c2, Rgb.White, (best - 0.92) * 4) : c2.Scale(best);
        }
    }
}

/// <summary>Soft blobs drifting slowly and melting into each other.</summary>
internal sealed class LavaLampEffect : Effect
{
    public static readonly EffectDefinition Definition = new(
        "lava", "Lava Lamp", "Soft, slow blobs drift and melt together.", "Animated",
        [
            new ColorParameter("blob", "Blobs", new Rgb(255, 60, 0)),
            new ColorParameter("glow", "Glow", new Rgb(255, 200, 0)),
            new ColorParameter("background", "Background", new Rgb(40, 0, 50)),
            new NumberParameter("speed", "Speed", 1, 10, 3),
            new NumberParameter("blobs", "Blobs", 2, 8, 5),
        ],
        () => new LavaLampEffect());

    public override void Render(in FrameContext frame, ReadOnlySpan<LedPoint> leds, Span<Rgb> output)
    {
        var s = Settings;
        double t = frame.Time * s.GetNumber("speed") * 0.08;
        int n = (int)s.GetNumber("blobs");
        Rgb blob = s.GetColor("blob"), glow = s.GetColor("glow"), background = s.GetColor("background");

        Span<(double X, double Y, double R)> blobs = stackalloc (double, double, double)[8];
        for (int b = 0; b < n; b++)
        {
            // Each blob wanders on its own slow Lissajous path, mostly rising and falling like wax.
            double k = b * 1.618;
            blobs[b] = (0.5 + 0.45 * Math.Sin(t * 0.7 + k * 2.1), 0.5 + 0.5 * Math.Sin(t * (0.9 + b * 0.07) + k), 0.13 + 0.05 * Math.Sin(k * 3));
        }

        for (int i = 0; i < leds.Length; i++)
        {
            double x = leds[i].X, y = leds[i].Y, field = 0;
            for (int b = 0; b < n; b++)
            {
                double dx = (x - blobs[b].X) * 1.6, dy = y - blobs[b].Y; // wide canvas: squash x
                field += blobs[b].R * blobs[b].R / (dx * dx + dy * dy + 1e-4);
            }
            // Inside a blob: wax color, hotter at the core. Near one: a glow fading into the background.
            output[i] = field >= 1
                ? Rgb.Lerp(blob, glow, Math.Min(1, (field - 1) * 0.35))
                : Rgb.Lerp(background, blob, Math.Pow(field, 3) * 0.8);
        }
    }
}

/// <summary>A glowing eye that sweeps back and forth, the classic scanner.</summary>
internal sealed class CylonEffect : Effect
{
    public static readonly EffectDefinition Definition = new(
        "cylon", "Cylon", "A glowing eye sweeps back and forth across your desk.", "Animated",
        [
            new ColorParameter("color", "Color", new Rgb(255, 40, 40)),
            new NumberParameter("speed", "Speed", 1, 10, 4),
            new NumberParameter("width", "Eye width", 3, 40, 12),
        ],
        () => new CylonEffect());

    public override void Render(in FrameContext frame, ReadOnlySpan<LedPoint> leds, Span<Rgb> output)
    {
        var s = Settings;
        double speed = s.GetNumber("speed") * 0.22, width = s.GetNumber("width") / 100;
        var color = s.GetColor("color");
        double sweep = frame.Time * speed % 2;
        double eye = 1 - Math.Abs(sweep - 1); // 0..1..0: the bounce

        for (int i = 0; i < leds.Length; i++)
        {
            double d = Math.Abs(leds[i].X - eye);
            double v = Math.Max(0, 1 - d / width);
            // A faint floor keeps sparse layouts lit while the eye is elsewhere.
            output[i] = v > 0.85 ? Rgb.Lerp(color, Rgb.White, (v - 0.85) * 5)
                : v > 0 ? color.Scale(v * v)
                : color.Scale(0.04);
        }
    }
}

/// <summary>A warm, wavering flame-colored glow, like a candle in the dark.</summary>
internal sealed class CandleEffect : Effect
{
    public static readonly EffectDefinition Definition = new(
        "candle", "Candle", "A warm, softly flickering glow.", "Animated",
        [
            new ColorParameter("color", "Flame", new Rgb(255, 138, 40)),
            new NumberParameter("speed", "Flicker", 1, 10, 4),
            new NumberParameter("gusts", "Gusts", 0, 10, 3),
        ],
        () => new CandleEffect());

    public override void Render(in FrameContext frame, ReadOnlySpan<LedPoint> leds, Span<Rgb> output)
    {
        var s = Settings;
        double t = frame.Time * s.GetNumber("speed");
        double gusts = s.GetNumber("gusts") / 10;
        var color = s.GetColor("color");

        // A slow gust that sways the whole flame, plus per-LED flicker layered from three
        // out-of-phase waves, so no two LEDs waver in step.
        double gust = 1 - gusts * 0.5 * (0.5 + 0.5 * Math.Sin(t * 0.9) * Math.Sin(t * 0.53 + 1.7));
        for (int i = 0; i < leds.Length; i++)
        {
            double seed = i * 2.399;
            double flicker = 0.72
                + 0.16 * Math.Sin(t * 4.3 + seed)
                + 0.09 * Math.Sin(t * 9.1 + seed * 3.1)
                + 0.05 * Math.Sin(t * 17.3 + seed * 7.7);
            // Warm the tips of the flame slightly hotter than its base.
            double height = 1 - leds[i].Y;
            var warm = Rgb.Lerp(color, new Rgb(255, 196, 90), height * 0.35);
            output[i] = warm.Scale(Math.Clamp(flicker * gust, 0.1, 1));
        }
    }
}
