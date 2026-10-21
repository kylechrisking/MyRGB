using MyRGB.Audio;
using MyRGB.Core.Audio;

namespace MyRGB.Tests;

/// <summary>Listens to this PC's output for a few seconds. Explicit; play something to see bands move.</summary>
public class LiveAudioTests(ITestOutputHelper output)
{
    [Fact(Explicit = true)]
    public async Task Listens_to_what_is_playing()
    {
        using var listener = new AudioListener();
        for (int i = 0; i < 40; i++)
        {
            var f = AudioFeed.Get(); // asking is what starts the listener
            if (i % 5 == 0)
                output.WriteLine($"level={f.Level:0.00} beats={f.Beats} bands=[{Bars(f.Bands)}] raw=[{Bars(f.Raw)}]");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        Assert.True(listener.Listening);
    }

    private static string Bars(float[] values) => string.Concat(values.Select(v => " .:-=+*#%@"[Math.Clamp((int)(v * 9.99), 0, 9)]));
}
