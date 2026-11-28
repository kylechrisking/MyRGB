using MyRGB.Core.Wallpaper;

namespace MyRGB.Core.Screen;

/// <summary>
/// Lets effects read what the main screen shows, as <see cref="WallpaperFeed"/> does for the wallpaper.
/// The screen is only copied while an effect has asked for it in the last couple of seconds, so
/// nothing watches the screen unless the person picked Match Screen.
/// </summary>
public static class ScreenFeed
{
    /// <summary>The id of the Match Screen effect (and its built-in preset), which reads this feed.</summary>
    public const string EffectId = "screen";

    private static volatile WallpaperFrame? _frame;
    private static long _lastAsked = long.MinValue / 2;

    /// <summary>The latest frame (null before the first), and a note that an effect wants it.</summary>
    public static WallpaperFrame? Get()
    {
        Volatile.Write(ref _lastAsked, Environment.TickCount64);
        return _frame;
    }

    /// <summary>True when an effect has read the screen within the last <paramref name="window"/>.</summary>
    public static bool Wanted(TimeSpan window) =>
        Environment.TickCount64 - Volatile.Read(ref _lastAsked) < window.TotalMilliseconds;

    public static void Publish(WallpaperFrame? frame) => _frame = frame;

    /// <summary>
    /// The frame without the black bars a film has above and below it (or at its sides), so the lights
    /// at the top and bottom take the picture's colors rather than going dark. A frame that is black all
    /// over, or has no bars, comes back as it is.
    /// </summary>
    public static WallpaperFrame TrimBlackBars(WallpaperFrame frame)
    {
        int w = frame.Width, h = frame.Height;
        bool RowDark(int y) { for (int x = 0; x < w; x++) if (!Dark(frame.Pixels[y * w + x])) return false; return true; }
        bool ColDark(int x) { for (int y = 0; y < h; y++) if (!Dark(frame.Pixels[y * w + x])) return false; return true; }

        int top = 0, bottom = h - 1, left = 0, right = w - 1;
        while (top < bottom && RowDark(top)) top++;
        while (bottom > top && RowDark(bottom)) bottom--;
        if (top == bottom) return frame; // a black screen: nothing to trim to
        while (left < right && ColDark(left)) left++;
        while (right > left && ColDark(right)) right--;
        // Only bars on both sides, about the same size, count; one dark edge is usually part of the picture.
        if (!Bars(top, h - 1 - bottom, h)) { top = 0; bottom = h - 1; }
        if (!Bars(left, w - 1 - right, w)) { left = 0; right = w - 1; }
        if (top == 0 && bottom == h - 1 && left == 0 && right == w - 1) return frame;

        int nw = right - left + 1, nh = bottom - top + 1;
        var pixels = new Rgb[nw * nh];
        for (int y = 0; y < nh; y++)
            Array.Copy(frame.Pixels, (top + y) * w + left, pixels, y * nw, nw);
        return new WallpaperFrame(nw, nh, pixels);
    }

    private static bool Bars(int a, int b, int size) => a > 0 && b > 0 && Math.Abs(a - b) <= Math.Max(1, size / 20);

    // Films' black is rarely exactly 0: allow a little, as compression leaves.
    private static bool Dark(Rgb c) => c.R < 16 && c.G < 16 && c.B < 16;
}
