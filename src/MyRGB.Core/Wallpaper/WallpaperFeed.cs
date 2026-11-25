namespace MyRGB.Core.Wallpaper;

/// <summary>
/// A small copy of what the desktop wallpaper shows right now: <see cref="Width"/> by <see cref="Height"/>
/// colors, row by row, spanning every screen. The lights' canvas maps onto it directly (0..1 both ways),
/// so a device placed on the left of the canvas takes its color from the left of the screens.
/// </summary>
public sealed record WallpaperFrame(int Width, int Height, Rgb[] Pixels)
{
    /// <summary>The average color of the box around (<paramref name="x"/>, <paramref name="y"/>), both 0..1;
    /// <paramref name="radius"/> is the box's half-size as a share of the width.</summary>
    public Rgb Sample(double x, double y, double radius)
    {
        int cx = (int)Math.Clamp(x * Width, 0, Width - 1), cy = (int)Math.Clamp(y * Height, 0, Height - 1);
        int r = Math.Max(0, (int)Math.Round(radius * Width));
        int x0 = Math.Max(0, cx - r), x1 = Math.Min(Width - 1, cx + r);
        int y0 = Math.Max(0, cy - r), y1 = Math.Min(Height - 1, cy + r);
        int sr = 0, sg = 0, sb = 0, n = 0;
        for (int row = y0; row <= y1; row++)
            for (int col = x0; col <= x1; col++)
            {
                var c = Pixels[row * Width + col];
                sr += c.R; sg += c.G; sb += c.B; n++;
            }
        return new Rgb((byte)(sr / n), (byte)(sg / n), (byte)(sb / n));
    }
}

/// <summary>
/// Lets effects read the wallpaper without a reference to the app, as <see cref="Audio.AudioFeed"/> does for
/// sound. The app publishes a frame whenever its own wallpaper draws one, or a still of Windows' own
/// wallpaper picture when MyRGB isn't showing one.
/// </summary>
public static class WallpaperFeed
{
    /// <summary>The id of the Match Wallpaper effect (and its built-in preset), which reads this feed.</summary>
    public const string EffectId = "wallpaper";

    private static volatile WallpaperFrame? _frame;
    private static long _lastAsked;

    /// <summary>The latest frame (null before the first), and a note that an effect wants it.</summary>
    public static WallpaperFrame? Get()
    {
        Volatile.Write(ref _lastAsked, Environment.TickCount64);
        return _frame;
    }

    /// <summary>The latest frame, without noting that anyone wants it.</summary>
    public static WallpaperFrame? Peek() => _frame;

    /// <summary>True when an effect has read the wallpaper within the last <paramref name="window"/>.</summary>
    public static bool Wanted(TimeSpan window) =>
        Environment.TickCount64 - Volatile.Read(ref _lastAsked) < window.TotalMilliseconds;

    public static void Publish(WallpaperFrame? frame) => _frame = frame;
}
