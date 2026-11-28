using MyRGB.Core.Screen;
using MyRGB.Core.Wallpaper;

namespace MyRGB.Core.Effects.BuiltIn;

/// <summary>
/// Lights take their colors from whatever the main screen shows (a game, a film, the desktop): each LED
/// from the part of the screen that matches its spot on the canvas. Works like Match Wallpaper, with the
/// screen in place of the wallpaper.
/// </summary>
internal sealed class ScreenEffect : Effect
{
    public const string Id = ScreenFeed.EffectId;

    public static readonly EffectDefinition Definition = new(
        Id, "Match Screen", "Your lights take their colors from what's on your main screen, like a game or a film, each from the part of the screen it sits by.", "Screen",
        [
            new NumberParameter("vivid", "Vividness", 0, 200, 130, 10),
            new NumberParameter("area", "Area each light looks at", 1, 30, 8),
            new NumberParameter("smooth", "Smoothing", 0, 100, 15, 5),
            new BoolParameter("bars", "Ignore black bars in films", true),
        ],
        () => new ScreenEffect());

    private Rgb[] _shown = [];
    private WallpaperFrame? _seen, _trimmed;

    public override void Render(in FrameContext frame, ReadOnlySpan<LedPoint> leds, Span<Rgb> output)
    {
        var s = Settings;
        double vivid = s.GetNumber("vivid") / 100, radius = s.GetNumber("area") / 100;
        double settle = s.GetNumber("smooth") / 100 * 1.0;
        double step = settle <= 0 ? 1 : Math.Min(1, frame.DeltaTime / settle * 3);
        if (_shown.Length != leds.Length) { _shown = new Rgb[leds.Length]; step = 1; }

        var screen = ScreenFeed.Get();
        if (!ReferenceEquals(screen, _seen))
        {
            _seen = screen;
            _trimmed = screen is not null && s.GetBool("bars") ? ScreenFeed.TrimBlackBars(screen) : screen;
        }
        var source = _trimmed;
        for (int i = 0; i < leds.Length; i++)
        {
            var target = source is null ? WallpaperEffect.Waiting : WallpaperEffect.Vivid(source.Sample(leds[i].X, leds[i].Y, radius), vivid);
            _shown[i] = Rgb.Lerp(_shown[i], target, step);
            output[i] = _shown[i];
        }
    }
}
