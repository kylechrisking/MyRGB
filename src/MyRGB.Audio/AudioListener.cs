using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyRGB.Core.Audio;
using NAudio.Wave;

namespace MyRGB.Audio;

/// <summary>
/// Listens to whatever the PC is playing (the default output, through WASAPI loopback: no admin, no
/// microphone, nothing recorded) and publishes it to <see cref="AudioFeed"/>. Only listens while an
/// effect has asked for audio in the last few seconds, so it costs nothing the rest of the time.
/// </summary>
public sealed class AudioListener : IDisposable
{
    private static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(10);

    private readonly ILogger _log;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private WasapiLoopbackCapture? _capture;
    private SpectrumAnalyzer? _analyzer;
    private long _lastData;
    private float[] _mono = [];
    private bool _disposed;

    public AudioListener(ILogger? log = null)
    {
        _log = log ?? NullLogger.Instance;
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(100));
    }

    public bool Listening
    {
        get { lock (_gate) return _capture is not null; }
    }

    private void Tick()
    {
        WasapiLoopbackCapture? finished = null;
        lock (_gate)
        {
            if (_disposed) return;
            bool wanted = AudioFeed.Wanted(IdleAfter);
            if (wanted && _capture is null) Start();
            else if (!wanted && _capture is not null) finished = Detach();
            // Loopback goes quiet when nothing is playing; feed silence so the bars fall back down.
            else if (_capture is not null && _analyzer is not null && Environment.TickCount64 - Volatile.Read(ref _lastData) > 150)
            {
                if (_analyzer.Push(new float[SpectrumAnalyzer.Hop])) AudioFeed.Publish(_analyzer.Frame);
            }
        }
        Shut(finished);
    }

    private void Start()
    {
        try
        {
            var capture = new WasapiLoopbackCapture();
            _analyzer = new SpectrumAnalyzer(capture.WaveFormat.SampleRate);
            capture.DataAvailable += OnData;
            capture.RecordingStopped += OnStopped;
            capture.StartRecording();
            _capture = capture;
            _log.LogInformation("Listening to audio for music effects ({Format})", capture.WaveFormat);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Couldn't listen to audio");
            _capture = null;
        }
    }

    /// <summary>Take the capture out of service (under the lock); <see cref="Shut"/> it after (outside).</summary>
    private WasapiLoopbackCapture? Detach()
    {
        var capture = _capture;
        _capture = null;
        if (capture is not null)
        {
            capture.DataAvailable -= OnData;
            capture.RecordingStopped -= OnStopped;
        }
        AudioFeed.Publish(AudioFrame.Silent);
        return capture;
    }

    /// <summary>Stopping waits for the capture thread, so never under the lock its handlers take.</summary>
    private void Shut(WasapiLoopbackCapture? capture)
    {
        if (capture is null) return;
        try { capture.StopRecording(); capture.Dispose(); }
        catch (Exception ex) { _log.LogDebug(ex, "Stopping audio capture"); }
    }

    /// <summary>The device went away or changed; the next tick starts again on the new default.</summary>
    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is { } ex) _log.LogInformation(ex, "Audio capture stopped");
        WasapiLoopbackCapture? finished = null;
        lock (_gate) if (ReferenceEquals(_capture, sender)) finished = Detach();
        _ = Task.Run(() => Shut(finished));
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (sender is not WasapiLoopbackCapture capture || e.BytesRecorded == 0) return;
        Volatile.Write(ref _lastData, Environment.TickCount64);
        var format = capture.WaveFormat;
        int channels = Math.Max(1, format.Channels), bytes = format.BitsPerSample / 8;
        int frames = e.BytesRecorded / (bytes * channels);
        if (_mono.Length < frames) _mono = new float[frames];

        // Mix down to mono. Loopback is almost always 32-bit float; 16- and 24-bit PCM just in case.
        var span = e.Buffer.AsSpan(0, e.BytesRecorded);
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
            {
                int o = (f * channels + c) * bytes;
                sum += bytes switch
                {
                    4 => BitConverter.ToSingle(span[o..]),
                    2 => BitConverter.ToInt16(span[o..]) / 32768f,
                    3 => (span[o] | span[o + 1] << 8 | (sbyte)span[o + 2] << 16) / 8388608f,
                    _ => 0,
                };
            }
            _mono[f] = sum / channels;
        }

        lock (_gate)
            if (_analyzer is not null && _analyzer.Push(_mono.AsSpan(0, frames))) AudioFeed.Publish(_analyzer.Frame);
    }

    public void Dispose()
    {
        WasapiLoopbackCapture? finished;
        lock (_gate)
        {
            _disposed = true;
            finished = Detach();
        }
        _timer.Dispose();
        Shut(finished);
    }
}
