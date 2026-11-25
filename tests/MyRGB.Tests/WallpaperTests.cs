using MyRGB.Core;
using MyRGB.Core.Effects;
using MyRGB.Core.Effects.BuiltIn;
using MyRGB.Core.Wallpaper;

namespace MyRGB.Tests;

[Collection("WallpaperFeed")] // the feed is process-wide
public class WallpaperTests
{
    // Left half red, right half blue.
    private static WallpaperFrame Split() => new(4, 2,
        [new(255, 0, 0), new(255, 0, 0), new(0, 0, 255), new(0, 0, 255),
         new(255, 0, 0), new(255, 0, 0), new(0, 0, 255), new(0, 0, 255)]);

    [Fact]
    public void Each_light_takes_the_color_of_its_part_of_the_screen()
    {
        WallpaperFeed.Publish(Split());
        try
        {
            var effect = WallpaperEffect.Definition.Create(new EffectSettings(new Dictionary<string, string> { ["vivid"] = "100", ["area"] = "1", ["smooth"] = "0" }));
            var output = new Rgb[2];
            effect.Render(new FrameContext(0, 1 / 30.0), [LedPoint.Whole(0.1f, 0.5f), LedPoint.Whole(0.9f, 0.5f)], output);
            Assert.Equal(new Rgb(255, 0, 0), output[0]);
            Assert.Equal(new Rgb(0, 0, 255), output[1]);
            Assert.True(WallpaperFeed.Wanted(TimeSpan.FromSeconds(5)));
        }
        finally { WallpaperFeed.Publish(null); }
    }

    [Fact]
    public void No_wallpaper_glows_dimly_and_sampling_averages_the_area()
    {
        WallpaperFeed.Publish(null);
        var effect = WallpaperEffect.Definition.Create(EffectSettings.Empty);
        var output = new Rgb[1];
        effect.Render(new FrameContext(0, 1), [LedPoint.Whole(0.5f, 0.5f)], output);
        Assert.Equal(WallpaperEffect.Waiting, output[0]);

        // A box across the middle sees both halves.
        var mixed = Split().Sample(0.5, 0.5, 0.5);
        Assert.True(mixed.R > 60 && mixed.B > 60, $"got {mixed}");
    }

    [Fact]
    public void Vividness_pushes_colors_away_from_grey()
    {
        var dull = new Rgb(140, 110, 110);
        var vivid = WallpaperEffect.Vivid(dull, 2);
        Assert.True(vivid.R - vivid.G > dull.R - dull.G);
        Assert.Equal(dull, WallpaperEffect.Vivid(dull, 1));
    }
}
