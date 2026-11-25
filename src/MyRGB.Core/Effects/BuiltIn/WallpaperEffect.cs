using MyRGB.Core.Wallpaper;

namespace MyRGB.Core.Effects.BuiltIn;

/// <summary>
/// Lights take their colors from the desktop wallpaper: each LED from the part of the screens that
/// matches its spot on the canvas. Works with MyRGB's moving wallpapers and with an ordinary picture.
/// </summary>
internal sealed class WallpaperEffect : Effect
{
    public const string Id = WallpaperFeed.EffectId;

    public static readonly EffectDefinition Definition = new(
        Id, "Match Wallpaper", "Your lights take their colors from your desktop wallpaper, each from the part of the screen it sits by.", "Screen",
        [
            new NumberParameter("vivid", "Vividness", 0, 200, 140, 10),
            new NumberParameter("area", "Area each light looks at", 1, 30, 6),
            new NumberParameter("smooth", "Smoothing", 0, 100, 40, 5),
        ],
        () => new WallpaperEffect());

    private Rgb[] _shown = [];

    /// <summary>A dim glow while there is no wallpaper to read yet, so the lights don't look dead.</summary>
    internal static readonly Rgb Waiting = new(28, 28, 32);

    public override void Render(in FrameContext frame, ReadOnlySpan<LedPoint> leds, Span<Rgb> output)
    {
        var s = Settings;
        double vivid = s.GetNumber("vivid") / 100, radius = s.GetNumber("area") / 100;
        // Smoothing is how long a change takes to settle: 0 follows at once, 100 takes about a second.
        double settle = s.GetNumber("smooth") / 100 * 1.0;
        double step = settle <= 0 ? 1 : Math.Min(1, frame.DeltaTime / settle * 3);
        if (_shown.Length != leds.Length) { _shown = new Rgb[leds.Length]; step = 1; }

        var wall = WallpaperFeed.Get();
        for (int i = 0; i < leds.Length; i++)
        {
            var target = wall is null ? Waiting : Vivid(wall.Sample(leds[i].X, leds[i].Y, radius), vivid);
            _shown[i] = Rgb.Lerp(_shown[i], target, step);
            output[i] = _shown[i];
        }
    }

    /// <summary>Push a color away from (or toward) its own grey: wallpapers read washed out on LEDs.</summary>
    internal static Rgb Vivid(Rgb c, double amount)
    {
        double grey = (c.R + c.G + c.B) / 3.0;
        byte Push(byte v) => (byte)Math.Clamp(grey + (v - grey) * amount, 0, 255);
        return new Rgb(Push(c.R), Push(c.G), Push(c.B));
    }
}
