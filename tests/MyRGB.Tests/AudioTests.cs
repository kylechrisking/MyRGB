using MyRGB.Audio;
using MyRGB.Core.Audio;

namespace MyRGB.Tests;

public class AudioTests
{
    private const int Rate = 48000;

    private static float[] Tone(double hz, double seconds, double amplitude = 0.5) =>
        Enumerable.Range(0, (int)(Rate * seconds)).Select(i => (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate))).ToArray();

    [Fact]
    public void A_tone_lights_the_band_it_falls_in()
    {
        var analyzer = new SpectrumAnalyzer(Rate);
        analyzer.Push(Tone(1000, 1));
        var bands = analyzer.Frame.Bands;

        // Bands are log-spaced from 40 Hz to 16 kHz; 1 kHz sits just over half way.
        int expected = (int)(Math.Log(1000 / 40.0) / Math.Log(16000 / 40.0) * AudioFrame.BandCount);
        int loudest = Array.IndexOf(bands, bands.Max());
        Assert.InRange(loudest, expected - 1, expected + 1);
        Assert.True(bands[2] < 0.5, "bass should stay low for a 1 kHz tone");
        Assert.True(analyzer.Frame.Level > 0.5);
    }

    [Fact]
    public void Silence_stays_dark()
    {
        var analyzer = new SpectrumAnalyzer(Rate);
        analyzer.Push(new float[Rate]);
        Assert.Equal(0, analyzer.Frame.Level);
        Assert.All(analyzer.Frame.Bands, b => Assert.True(b < 0.05));
    }

    [Fact]
    public void Regular_bass_thumps_count_as_kicks()
    {
        var analyzer = new SpectrumAnalyzer(Rate);
        // Two seconds of a quiet bed, then a 60 Hz thump every half second for four seconds.
        analyzer.Push(Tone(300, 2, 0.02));
        for (int i = 0; i < 8; i++)
        {
            analyzer.Push(Tone(60, 0.1, 0.8));
            analyzer.Push(Tone(300, 0.4, 0.02));
        }
        Assert.InRange(analyzer.Frame.Beats, 6, 9);
    }

    [Fact]
    public void Visualizer_draws_bars_and_brings_the_background_up_in_silence()
    {
        var def = MyRGB.Core.Effects.EffectRegistry.CreateDefault().Find("visualizer")!;
        var effect = def.Create(new MyRGB.Core.Effects.EffectSettings(new Dictionary<string, string> { ["background"] = "White", ["bgBrightness"] = "0" }));
        // Two LEDs at the bottom: one under the bass end, one under the treble end.
        MyRGB.Core.Effects.LedPoint[] leds = [MyRGB.Core.Effects.LedPoint.Whole(0.01f, 0.99f), MyRGB.Core.Effects.LedPoint.Whole(0.99f, 0.99f)];
        var output = new MyRGB.Core.Rgb[2];
        try
        {
            var loudBass = new float[AudioFrame.BandCount];
            loudBass[0] = loudBass[1] = 1;
            AudioFeed.Publish(new AudioFrame(1, loudBass, 0, loudBass));
            effect.Render(new MyRGB.Core.Effects.FrameContext(0, 1 / 30.0), leds, output);
            Assert.True(output[0].G == 255 && output[0].R < 20 && output[0].B == 0, $"{output[0]}"); // a bar, green at the bottom
            Assert.Equal(MyRGB.Core.Rgb.Black, output[1]);          // no treble, background off

            AudioFeed.Publish(AudioFrame.Silent);
            for (int f = 0; f < 30 * 6; f++) effect.Render(new MyRGB.Core.Effects.FrameContext(f / 30.0, 1 / 30.0), leds, output);
            Assert.Equal(new MyRGB.Core.Rgb(255, 255, 255), output[1]); // after the timeout, the background is up
        }
        finally { AudioFeed.Publish(AudioFrame.Silent); }
    }

    [Fact]
    public void Fft_finds_a_pure_bin()
    {
        int n = 64;
        var re = Enumerable.Range(0, n).Select(i => Math.Cos(2 * Math.PI * 5 * i / n)).ToArray();
        var im = new double[n];
        SpectrumAnalyzer.Fft(re, im);
        var mags = re.Zip(im, (r, i) => Math.Sqrt(r * r + i * i)).ToArray();
        Assert.Equal(5, Array.IndexOf(mags, mags.Take(n / 2).Max()));
    }
}
