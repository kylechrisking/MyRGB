using MyRGB.Core.Devices;

namespace MyRGB.Tests;

public class ScreenFitTests
{
    [Fact]
    public void A_wide_picture_loses_its_sides()
    {
        // 400×200 onto 248×170: the height fills, the sides are cropped evenly.
        var (x, y, w, h) = ScreenFit.Cover(400, 200, 248, 170);
        Assert.Equal(0, y, 6);
        Assert.Equal(200, h, 6);
        Assert.Equal(200 * 248 / 170.0, w, 6);
        Assert.Equal((400 - w) / 2, x, 6);
    }

    [Fact]
    public void A_tall_picture_loses_its_top_and_bottom()
    {
        var (x, y, w, h) = ScreenFit.Cover(1000, 2000, 248, 170);
        Assert.Equal(0, x, 6);
        Assert.Equal(1000, w, 6);
        Assert.Equal(1000 * 170 / 248.0, h, 6);
        Assert.Equal((2000 - h) / 2, y, 6);
    }

    [Fact]
    public void A_picture_the_screens_shape_is_used_whole()
    {
        Assert.Equal((0d, 0d, 496d, 340d), ScreenFit.Cover(496, 340, 248, 170));
    }

    [Fact]
    public void Bgra_becomes_rgb_and_row_padding_is_skipped()
    {
        // 2×2, stride 12 (4 bytes of padding per row).
        byte[] bgra =
        [
            1, 2, 3, 255,   4, 5, 6, 255,   9, 9, 9, 9,
            7, 8, 9, 128,   10, 11, 12, 0,  9, 9, 9, 9,
        ];
        Assert.Equal([3, 2, 1, 6, 5, 4, 9, 8, 7, 12, 11, 10], ScreenFit.PremultipliedBgraToRgb(bgra, 2, 2, 12));
    }

    [Fact]
    public void Too_small_a_pixel_buffer_is_refused()
    {
        Assert.Throws<ArgumentException>(() => ScreenFit.PremultipliedBgraToRgb(new byte[7], 2, 1, 8));
    }
}
