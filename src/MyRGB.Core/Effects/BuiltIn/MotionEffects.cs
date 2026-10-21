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
