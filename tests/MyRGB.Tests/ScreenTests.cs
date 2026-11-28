using MyRGB.Core;
using MyRGB.Core.Effects;
using MyRGB.Core.Effects.BuiltIn;
using MyRGB.Core.Screen;
using MyRGB.Core.Wallpaper;

namespace MyRGB.Tests;

[Collection("ScreenFeed")] // the feed is process-wide
public class ScreenTests
{
    private static readonly Rgb Black = new(0, 0, 0), Red = new(255, 0, 0), Blue = new(0, 0, 255);

    // A film: a black bar on top and bottom, red on the left half and blue on the right in between.
    private static WallpaperFrame Film() => new(4, 4,
        [Black, Black, Black, Black,
         Red, Red, Blue, Blue,
         Red, Red, Blue, Blue,
         Black, Black, Black, Black]);

    [Fact]
    public void Black_bars_above_and_below_a_film_are_skipped()
    {
        var trimmed = ScreenFeed.TrimBlackBars(Film());
        Assert.Equal(4, trimmed.Width);
        Assert.Equal(2, trimmed.Height);
        Assert.Equal(Red, trimmed.Sample(0, 0, 0));
        Assert.Equal(Blue, trimmed.Sample(1, 1, 0));
    }

    [Fact]
    public void A_dark_edge_on_one_side_only_is_kept_and_a_black_screen_stays_black()
    {
        var oneSided = new WallpaperFrame(2, 4, [Black, Black, Red, Red, Red, Red, Red, Red]);
        Assert.Same(oneSided, ScreenFeed.TrimBlackBars(oneSided));
        var black = new WallpaperFrame(2, 2, [Black, Black, Black, Black]);
        Assert.Same(black, ScreenFeed.TrimBlackBars(black));
    }

    [Fact]
    public void Each_light_takes_the_color_of_its_part_of_the_screen()
    {
        ScreenFeed.Publish(Film());
        try
        {
            var effect = ScreenEffect.Definition.Create(new EffectSettings(new Dictionary<string, string> { ["vivid"] = "100", ["area"] = "1", ["smooth"] = "0" }));
            var output = new Rgb[2];
            // At the very top: with the bars skipped, the lights there see the picture, not black.
            effect.Render(new FrameContext(0, 1 / 30.0), [LedPoint.Whole(0.1f, 0f), LedPoint.Whole(0.9f, 0f)], output);
            Assert.Equal(Red, output[0]);
            Assert.Equal(Blue, output[1]);
            Assert.True(ScreenFeed.Wanted(TimeSpan.FromSeconds(5)));
        }
        finally { ScreenFeed.Publish(null); }
    }

    [Fact]
    public void Nothing_on_the_feed_glows_dimly()
    {
        ScreenFeed.Publish(null);
        var effect = ScreenEffect.Definition.Create(EffectSettings.Empty);
        var output = new Rgb[1];
        effect.Render(new FrameContext(0, 1), [LedPoint.Whole(0.5f, 0.5f)], output);
        Assert.Equal(WallpaperEffect.Waiting, output[0]);
    }
}
