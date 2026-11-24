namespace MyRGB.Core.Audio;

/// <summary>
/// What the PC is playing right now, as music effects see it. <see cref="Bands"/> run from bass to
/// treble, each 0..1 and scaled to the recent loudness of that band, so quiet and loud music both
/// fill the range. <see cref="Beats"/> counts kicks since listening started. <see cref="Raw"/> is the same
/// bands on a fixed scale instead (each band's loudest frequency, -65 dB to -20 dB as 0..1, no auto-gain or
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

    /// <summary>A stand-in frame for this thread only (effect thumbnails), so they move without real audio.</summary>
    [ThreadStatic] private static AudioFrame? _pretend;

    /// <summary>The latest frame, and a note that an effect wants audio.</summary>
    public static AudioFrame Get()
    {
        if (_pretend is { } pretend) return pretend; // a thumbnail: doesn't ask the host to listen
        Volatile.Write(ref _lastAsked, Environment.TickCount64);
        return _frame;
    }

    /// <summary>The latest frame, without asking the host to keep listening.</summary>
    public static AudioFrame Peek() => _frame;

    /// <summary>True when an effect has asked for audio within the last <paramref name="window"/>.</summary>
    public static bool Wanted(TimeSpan window) =>
        Environment.TickCount64 - Volatile.Read(ref _lastAsked) < window.TotalMilliseconds;

    /// <summary>
    /// Until disposed, <see cref="Get"/> on this thread returns <paramref name="frame"/> instead of the real
    /// audio. Effect thumbnails use it to show a music effect moving; the lights (another thread) never see it.
    /// </summary>
    public static IDisposable Pretend(AudioFrame frame)
    {
        var before = _pretend;
        _pretend = frame;
        return new Restore(before);
    }

    private sealed class Restore(AudioFrame? before) : IDisposable
    {
        public void Dispose() => _pretend = before;
    }

    /// <summary>Called by the capture side with each new analysis.</summary>
    public static void Publish(AudioFrame frame) => _frame = frame;
}
