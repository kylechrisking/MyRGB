namespace MyRGB.Core.Audio;

/// <summary>
/// What the PC is playing right now, as music effects see it. <see cref="Bands"/> run from bass to
/// treble, each 0..1 and scaled to the recent loudness of that band, so quiet and loud music both
/// fill the range. <see cref="Beats"/> counts kicks since listening started. <see cref="Raw"/> is the same
/// bands on a fixed scale instead (each band's loudest frequency, -50 dB to -5 dB as 0..1, no auto-gain or
/// smoothing), for effects that do their own, like the Visualizer.
/// </summary>
public sealed record AudioFrame(float Level, float[] Bands, long Beats, float[] Raw)
{
    public const int BandCount = 32;
    public static readonly AudioFrame Silent = new(0, new float[BandCount], 0, new float[BandCount]);
}

/// <summary>
/// Lets effects read the audio without a reference to the app. Listening is on demand: effects
/// call <see cref="Get"/> each frame, and the host only captures while someone has asked recently.
/// </summary>
public static class AudioFeed
{
    private static volatile AudioFrame _frame = AudioFrame.Silent;
    private static long _lastAsked;

    /// <summary>The latest frame, and a note that an effect wants audio.</summary>
    public static AudioFrame Get()
    {
        Volatile.Write(ref _lastAsked, Environment.TickCount64);
        return _frame;
    }

    /// <summary>True when an effect has asked for audio within the last <paramref name="window"/>.</summary>
    public static bool Wanted(TimeSpan window) =>
        Environment.TickCount64 - Volatile.Read(ref _lastAsked) < window.TotalMilliseconds;

    /// <summary>Called by the capture side with each new analysis.</summary>
    public static void Publish(AudioFrame frame) => _frame = frame;
}
