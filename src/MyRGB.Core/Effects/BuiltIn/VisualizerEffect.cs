using MyRGB.Core.Audio;

namespace MyRGB.Core.Effects.BuiltIn;

/// <summary>
/// A full audio visualizer in the style of OpenRGB's Keyboard Visualizer: the desk is one picture,
/// a background pattern glows behind it, and the music draws over it in a foreground pattern, as
/// frequency bars (Spectrograph), one bass bar (Bar), or the whole desk at once (Single color).
/// Everything about the response is tunable: amplitude, decay, averaging, bass and treble balance,
/// smoothing, and how long silence lasts before the background comes up.
/// </summary>
/// <remarks>Written for MyRGB from the behaviour of that program; it shares no code with it.</remarks>
internal sealed class VisualizerEffect : Effect
{
    public static readonly string[] Patterns =
    [
        "Green/Yellow/Red", "Green/White/Red", "Blue/Cyan/White", "Red/White/Blue",
        "Rainbow Bars", "Rainbow Bars Inverse", "Rainbow", "Color Wheel", "Spectrum Cycle",
        "Black", "White", "Red", "Orange", "Yellow", "Green", "Cyan", "Blue", "Purple",
    ];

    public static readonly EffectDefinition Definition = new(
        "visualizer", "Visualizer", "Your desk as an audio visualizer: frequency bars, a bass bar or a pulsing glow over an animated background.", "Music",
        [
            new ChoiceParameter("style", "Style", ["Spectrograph", "Bar", "Single color"], "Spectrograph"),
            new ChoiceParameter("drawOn", "Draw on", ["Each device", "Whole desk"], "Each device"),
            new ChoiceParameter("foreground", "Foreground", Patterns, "Green/Yellow/Red"),
            new ChoiceParameter("background", "Background", Patterns, "Rainbow"),
            new NumberParameter("bgBrightness", "Background brightness %", 0, 100, 10),
            new NumberParameter("amplitude", "Amplitude %", 10, 400, 100, 5),
            new NumberParameter("decay", "Decay %", 0, 98, 80),
            new NumberParameter("average", "Average size", 1, 8, 1),
            new ChoiceParameter("averageMode", "Average mode", ["Binning", "Smooth"], "Binning"),
            new NumberParameter("bass", "Bass level %", 10, 200, 70, 5),
            new NumberParameter("treble", "Treble boost %", 0, 300, 100, 5),
            new NumberParameter("filter", "Smoothing %", 0, 95, 0, 5),
            new NumberParameter("speed", "Animation speed %", -200, 200, 100, 10),
            new NumberParameter("timeout", "Background after silence (s)", 0, 30, 3),
        ],
        () => new VisualizerEffect());

    private readonly float[] _shown = new float[AudioFrame.BandCount];
    private double _quietFor, _bgFade;

    public override void Render(in FrameContext frame, ReadOnlySpan<LedPoint> leds, Span<Rgb> output)
    {
        var s = Settings;
        var audio = AudioFeed.Get();
        double dt = Math.Max(frame.DeltaTime, 0), t = frame.Time * s.GetNumber("speed") / 100;

        // Response: per-band gain (bass level at the left rising by the treble boost to the right),
        // optional input smoothing, then decay: each bar falls no faster than its decay allows.
        double amplitude = s.GetNumber("amplitude") / 100, bass = s.GetNumber("bass") / 100, treble = s.GetNumber("treble") / 100;
        double keep = Math.Pow(s.GetNumber("decay") / 100, dt * 30), filter = 1 - s.GetNumber("filter") / 100;
        var bars = Average(audio.Raw, (int)s.GetNumber("average"), s.GetString("averageMode") == "Smooth");
        double loudest = 0;
        for (int b = 0; b < bars.Length; b++)
        {
            double pos = (double)b / (bars.Length - 1);
            double target = Math.Clamp(bars[b] * amplitude * (bass + treble * pos * 0.5), 0, 1);
            double filtered = _shown[b] + (target - _shown[b]) * filter;
            _shown[b] = (float)Math.Max(filtered, _shown[b] * keep);
            loudest = Math.Max(loudest, target);
        }

        // After a stretch of silence the background fades up to full, like a screensaver.
        _quietFor = loudest < 0.03 ? _quietFor + dt : 0;
        double timeout = s.GetNumber("timeout");
        _bgFade = Math.Clamp(_bgFade + (_quietFor > timeout ? dt : -dt * 3) / 1.5, 0, 1);
        double bgLevel = s.GetNumber("bgBrightness") / 100;
        bgLevel += (1 - bgLevel) * _bgFade;

        string style = s.GetString("style"), fg = s.GetString("foreground"), bg = s.GetString("background");
        bool eachDevice = s.GetString("drawOn") == "Each device";
        double bassLevel = (_shown[0] + _shown[1] + _shown[2] + _shown[3]) / 4;

        for (int i = 0; i < leds.Length; i++)
        {
            // "Each device" draws the whole picture on every device, the way Keyboard Visualizer does;
            // "Whole desk" spreads one picture across them all.
            double x = eachDevice ? leds[i].LocalX : leds[i].X, up = 1 - (eachDevice ? leds[i].LocalY : leds[i].Y);
            var back = Pattern(bg, x, up, t).Scale(bgLevel);
            output[i] = style switch
            {
                "Bar" => x <= bassLevel ? Pattern(fg, x, up, t) : back,
                "Single color" => Rgb.Max(back, Pattern(fg, 0.5, bassLevel, t).Scale(bassLevel)),
                _ => up <= _shown[Math.Clamp((int)(x * _shown.Length), 0, _shown.Length - 1)] ? Pattern(fg, x, up, t) : back,
            };
        }
    }

    /// <summary>Merge neighbouring bands: into blocks of <paramref name="size"/>, or a running average.</summary>
    internal static float[] Average(float[] bands, int size, bool smooth)
    {
        var result = new float[bands.Length];
        if (size <= 1) { bands.CopyTo(result, 0); return result; }
        for (int b = 0; b < bands.Length; b++)
        {
            int from = smooth ? Math.Max(0, b - size / 2) : b / size * size;
            int to = Math.Min(bands.Length, smooth ? from + size : from + size);
            float sum = 0;
            for (int k = from; k < to; k++) sum += bands[k];
            result[b] = sum / (to - from);
        }
        return result;
    }

    /// <summary>A pattern's color at a point: gradients run bottom to top, rainbows across, the rest move with time.</summary>
    internal static Rgb Pattern(string name, double x, double up, double t) => name switch
    {
        "Green/Yellow/Red" => Gradient(up, new(0, 255, 0), new(255, 255, 0), new(255, 0, 0)),
        "Green/White/Red" => Gradient(up, new(0, 255, 0), new(255, 255, 255), new(255, 0, 0)),
        "Blue/Cyan/White" => Gradient(up, new(0, 0, 255), new(0, 255, 255), new(255, 255, 255)),
        "Red/White/Blue" => Gradient(up, new(255, 0, 0), new(255, 255, 255), new(0, 0, 255)),
        "Rainbow Bars" => Rgb.FromHsv(x * 300, 1, 1),
        "Rainbow Bars Inverse" => Rgb.FromHsv((1 - x) * 300, 1, 1),
        "Rainbow" => Rgb.FromHsv(x * 360 - t * 60, 1, 1),
        "Color Wheel" => Rgb.FromHsv(Math.Atan2(up - 0.5, (x - 0.5) * 1.78) * 180 / Math.PI + t * 60, 1, 1),
        "Spectrum Cycle" => Rgb.FromHsv(t * 40, 1, 1),
        "White" => new(255, 255, 255),
        "Red" => new(255, 0, 0),
        "Orange" => new(255, 100, 0),
        "Yellow" => new(255, 220, 0),
        "Green" => new(0, 255, 0),
        "Cyan" => new(0, 255, 255),
        "Blue" => new(0, 0, 255),
        "Purple" => new(160, 0, 255),
        _ => Rgb.Black,
    };

    private static Rgb Gradient(double up, Rgb low, Rgb mid, Rgb high) =>
        up < 0.5 ? Rgb.Lerp(low, mid, up * 2) : Rgb.Lerp(mid, high, (up - 0.5) * 2);
}
