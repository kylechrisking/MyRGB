using MyRGB.Core;
using MyRGB.Core.Effects;

namespace MyRGB.Tests;

/// <summary>Effect thumbnails show a still frame; every effect should have something lit in its first few seconds.</summary>
public class EffectStillTests
{
    [Theory]
    [InlineData("ripple")]
    [InlineData("twinkle")]
    public void Lights_something_in_the_first_seconds(string id)
    {
        var effect = EffectRegistry.CreateDefault().Find(id)!.Create(new EffectSettings(null));
        const int cols = 33, rows = 10;
        var points = new LedPoint[cols * rows];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                points[r * cols + c] = LedPoint.Whole((c + 0.5f) / cols, (r + 0.5f) / rows);
        var colors = new Rgb[points.Length];

        long best = 0;
        for (double t = 0; t <= 4; t += 1 / 15.0)
        {
            effect.Render(new FrameContext(t, 1 / 15.0), points, colors);
            best = Math.Max(best, colors.Sum(c => (long)c.R + c.G + c.B));
        }
        Assert.True(best > 2000, $"{id}: brightest frame summed to {best}");
    }
}
