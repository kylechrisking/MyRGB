using MyRGB.Core.Audio;

namespace MyRGB.Core.Effects.BuiltIn;

/// <summary>Shared bits for the effects that follow the music.</summary>
internal static class Music
{
    /// <summary>True when nothing is playing: effects show a quiet idle animation instead of going dark.</summary>
    public static bool Silent(AudioFrame f) => f.Level < 0.02f && f.Bands.Max() < 0.05f;

    /// <summary>Sensitivity 1..10 as a gain on a 0..1 value.</summary>
    public static double Gain(double value, double sensitivity) => Math.Clamp(value * (0.4 + sensitivity * 0.16), 0, 1);
}

internal sealed class MusicPulseEffect : Effect
{
    public static readonly EffectDefinition Definition = new(
        "audio-pulse", "Music Pulse", "Everything breathes with the music and changes color on every kick.", "Music",
        [
            new ChoiceParameter("colors", "Colors", ["Rainbow", "One color"], "Rainbow"),
            new ColorParameter("color", "Color", new Rgb(255, 0, 120)),
            new NumberParameter("sensitivity", "Sensitivity", 1, 10, 5),
            new NumberParameter("floor", "Lowest brightness %", 0, 50, 6),
        ],
        () => new MusicPulseEffect());

    private long _beats = -1;
    private double _hue = Random.Shared.Next(360);

    public override void Render(in FrameContext frame, ReadOnlySpan<LedPoint> leds, Span<Rgb> output)
    {
        var s = Settings;
        var audio = AudioFeed.Get();
        if (_beats >= 0 && audio.Beats != _beats) _hue = (_hue + 47) % 360; // a fresh color each kick
        _beats = audio.Beats;

        double floor = s.GetNumber("floor") / 100;
        double level = Music.Silent(audio)
            ? 0.15 + 0.1 * Math.Sin(frame.Time * 1.2) // waiting for sound
            : Music.Gain(audio.Level, s.GetNumber("sensitivity"));
        var color = s.GetString("colors") == "One color" ? s.GetColor("color") : Rgb.FromHsv(_hue, 1, 1);
        output.Fill(color.Scale(floor + (1 - floor) * level));
    }
}

internal sealed class MusicWavesEffect : Effect
{
    public static readonly EffectDefinition Definition = new(
        "audio-waves", "Music Waves", "Every kick sends a ring of color out across the desk.", "Music",
        [
            new NumberParameter("speed", "Wave speed", 1, 10, 5),
            new NumberParameter("width", "Wave width", 5, 60, 18),
            new ColorParameter("background", "Background", new Rgb(10, 0, 30)),
        ],
        () => new MusicWavesEffect());

    private readonly List<(double Born, double Hue, double Strength)> _waves = [];
    private long _beats = -1;
    private double _hue = Random.Shared.Next(360), _idleNext;

    public override void Render(in FrameContext frame, ReadOnlySpan<LedPoint> leds, Span<Rgb> output)
    {
        var s = Settings;
        var audio = AudioFeed.Get();
        double t = frame.Time, speed = s.GetNumber("speed") * 0.12, width = s.GetNumber("width") / 100;

        bool kick = _beats >= 0 && audio.Beats != _beats;
        _beats = audio.Beats;
        // While silent, a slow wave every couple of seconds shows the effect is alive.
        if (Music.Silent(audio) && t >= _idleNext) { kick = true; _idleNext = t + 2.2; }
        if (kick)
        {
            _hue = (_hue + 53) % 360;
            _waves.Add((t, _hue, Music.Silent(audio) ? 0.5 : 0.6 + 0.4 * audio.Level));
        }
        _waves.RemoveAll(w => (t - w.Born) * speed > 1.6);

        var background = s.GetColor("background");
        for (int i = 0; i < leds.Length; i++)
        {
            double dx = leds[i].X - 0.5, dy = (leds[i].Y - 0.5) * 0.6; // the desk is wide, so rings are too
            double r = Math.Sqrt(dx * dx + dy * dy) * 2;
            var c = background;
            foreach (var w in _waves)
            {
                double front = (t - w.Born) * speed, d = Math.Abs(r - front);
                if (d < width) c = Rgb.Max(c, Rgb.FromHsv(w.Hue, 1, 1).Scale(w.Strength * (1 - d / width) * Math.Max(0, 1 - front / 1.6)));
            }
            output[i] = c;
        }
    }
}
