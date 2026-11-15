namespace MyRGB.Core.Devices;

/// <summary>A device with a small screen MyRGB can put a picture on, such as the Vanguard Pro 96's LCD.</summary>
public interface IScreenDevice : IRgbDevice
{
    int ScreenWidth { get; }
    int ScreenHeight { get; }

    /// <summary>True while a picture MyRGB put there is stored on the device.</summary>
    bool HasScreenImage { get; }

    /// <summary>
    /// Stores a picture and selects it, replacing the last one MyRGB put there. <paramref name="rgb"/> is
    /// <see cref="ScreenWidth"/> × <see cref="ScreenHeight"/> R,G,B triples, top row first.
    /// </summary>
    Task SetScreenImageAsync(ReadOnlyMemory<byte> rgb, CancellationToken ct = default);

    /// <summary>Takes MyRGB's picture off again and puts back what the device had before.</summary>
    Task ClearScreenImageAsync(CancellationToken ct = default);

    /// <summary>
    /// Some screens only show while the device runs its own lighting. While this is true the device has
    /// its lighting back so the screen shows, and MyRGB's colors don't reach it.
    /// </summary>
    bool ScreenFirst { get; }

    Task SetScreenFirstAsync(bool on, CancellationToken ct = default);
}

/// <summary>Fitting a picture onto a device screen.</summary>
public static class ScreenFit
{
    /// <summary>
    /// The part of a <paramref name="srcW"/> × <paramref name="srcH"/> picture that fills a
    /// <paramref name="dstW"/> × <paramref name="dstH"/> screen without stretching: the largest centered
    /// rectangle with the screen's shape. Returned as (x, y, width, height) in source pixels.
    /// </summary>
    public static (double X, double Y, double W, double H) Cover(double srcW, double srcH, double dstW, double dstH)
    {
        if (srcW <= 0 || srcH <= 0 || dstW <= 0 || dstH <= 0) throw new ArgumentOutOfRangeException(nameof(srcW), "Sizes must be positive.");
        double scale = Math.Max(dstW / srcW, dstH / srcH);
        double w = dstW / scale, h = dstH / scale;
        return ((srcW - w) / 2, (srcH - h) / 2, w, h);
    }

    /// <summary>
    /// R,G,B triples from premultiplied B,G,R,A pixels, as if drawn on black (which is what premultiplied
    /// color already is). <paramref name="stride"/> is the bytes per source row.
    /// </summary>
    public static byte[] PremultipliedBgraToRgb(ReadOnlySpan<byte> bgra, int width, int height, int stride)
    {
        if (stride < width * 4 || bgra.Length < stride * (height - 1) + width * 4) throw new ArgumentException("Pixel buffer too small.", nameof(bgra));
        var rgb = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            var row = bgra.Slice(y * stride, width * 4);
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 3;
                rgb[o] = row[x * 4 + 2];
                rgb[o + 1] = row[x * 4 + 1];
                rgb[o + 2] = row[x * 4];
            }
        }
        return rgb;
    }
}
