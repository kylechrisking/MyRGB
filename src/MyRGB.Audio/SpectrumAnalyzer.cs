using MyRGB.Core.Audio;

namespace MyRGB.Audio;

/// <summary>
/// Turns a stream of mono samples into <see cref="AudioFrame"/>s: 32 bands spaced like hearing is
/// (40 Hz to 16 kHz, evenly on a log scale), auto-gained together against their recent peak, an overall
/// level, and kicks found in the bass. One frame per <see cref="Hop"/> samples.
/// </summary>
public sealed class SpectrumAnalyzer
{
    public const int FftSize = 2048, Hop = 1024;
    private const double MinHz = 40, MaxHz = 16000;

    private readonly float[] _ring = new float[FftSize];
    private readonly double[] _re = new double[FftSize], _im = new double[FftSize];
    private readonly double[] _window = new double[FftSize];
    private readonly (int From, int To)[] _bands = new (int, int)[AudioFrame.BandCount];
    private readonly float[] _shown = new float[AudioFrame.BandCount];
    private readonly double[] _bassHistory = new double[48]; // about a second of hops at 48 kHz
    private readonly double _hopSeconds;
    private int _filled, _sinceHop, _historyAt, _write;
    private double _levelPeak = 1e-4, _spectrumPeak = 0.02, _sinceBeat = 1;
    private long _beats;
    private float _level;

    public SpectrumAnalyzer(int sampleRate)
    {
        _hopSeconds = (double)Hop / sampleRate;
        for (int i = 0; i < FftSize; i++) _window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1)); // Hann
        double binHz = (double)sampleRate / FftSize;
        for (int b = 0; b < AudioFrame.BandCount; b++)
        {
            double lo = MinHz * Math.Pow(MaxHz / MinHz, (double)b / AudioFrame.BandCount);
            double hi = MinHz * Math.Pow(MaxHz / MinHz, (double)(b + 1) / AudioFrame.BandCount);
            int from = Math.Clamp((int)(lo / binHz), 1, FftSize / 2 - 1);
            int to = Math.Clamp((int)Math.Ceiling(hi / binHz), from + 1, FftSize / 2);
            _bands[b] = (from, to);
        }
    }

    public AudioFrame Frame { get; private set; } = AudioFrame.Silent;

    /// <summary>Add samples; returns true when a new <see cref="Frame"/> is ready.</summary>
    public bool Push(ReadOnlySpan<float> samples)
    {
        bool updated = false;
        foreach (var s in samples)
        {
            _ring[_write] = s;
            _write = (_write + 1) % FftSize;
            if (_filled < FftSize) _filled++;
            if (++_sinceHop >= Hop && _filled == FftSize)
            {
                _sinceHop = 0;
                Analyze();
                updated = true;
            }
        }
        return updated;
    }

    private void Analyze()
    {
        double sum = 0;
        for (int i = 0; i < FftSize; i++)
        {
            float x = _ring[(_write + i) % FftSize]; // oldest first
            _re[i] = x * _window[i];
            _im[i] = 0;
            sum += x * x;
        }
        Fft(_re, _im);

        var raw = new double[AudioFrame.BandCount];
        double bass = 0, loudest = 0;
        for (int b = 0; b < AudioFrame.BandCount; b++)
        {
            var (from, to) = _bands[b];
            double energy = 0;
            for (int k = from; k < to; k++) energy += _re[k] * _re[k] + _im[k] * _im[k];
            double v = Math.Log10(1 + Math.Sqrt(energy / (to - from)) * 10);
            if (b < 4) bass += v;
            // Music carries far less energy up top; tilt the treble up so it shows at all.
            raw[b] = v * (1 + 1.5 * b / AudioFrame.BandCount);
            loudest = Math.Max(loudest, raw[b]);
        }

        // All bands against one recent peak, which slowly forgets so the scale follows the music.
        // (Per-band peaks would push faint bands, even leakage, all the way up.)
        _spectrumPeak = Math.Max(loudest, Math.Max(_spectrumPeak * 0.997, 0.02));
        var bands = new float[AudioFrame.BandCount];
        for (int b = 0; b < AudioFrame.BandCount; b++)
        {
            float target = (float)Math.Clamp(raw[b] / _spectrumPeak, 0, 1);
            // Jump up fast, fall back slowly: reads as motion rather than flicker.
            _shown[b] = target > _shown[b] ? _shown[b] + (target - _shown[b]) * 0.7f : _shown[b] + (target - _shown[b]) * 0.18f;
            bands[b] = _shown[b];
        }

        double rms = Math.Sqrt(sum / FftSize);
        _levelPeak = Math.Max(rms, Math.Max(_levelPeak * 0.997, 0.005));
        float level = (float)Math.Clamp(rms / _levelPeak, 0, 1);
        _level = level > _level ? level : _level + (level - _level) * 0.2f;

        // A kick: bass well above its average over the last second, and not too soon after the last.
        double average = _bassHistory.Average();
        _bassHistory[_historyAt++ % _bassHistory.Length] = bass;
        _sinceBeat += _hopSeconds;
        if (bass > average * 1.35 && bass > 0.08 && _sinceBeat > 0.25)
        {
            _beats++;
            _sinceBeat = 0;
        }

        Frame = new AudioFrame(rms < 1e-5 ? 0 : _level, bands, _beats);
    }

    /// <summary>In-place radix-2 FFT. <paramref name="re"/>.Length must be a power of two.</summary>
    internal static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2 * Math.PI / len;
            double wr = Math.Cos(angle), wi = Math.Sin(angle);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    (cr, ci) = (cr * wr - ci * wi, cr * wi + ci * wr);
                }
            }
        }
    }
}
