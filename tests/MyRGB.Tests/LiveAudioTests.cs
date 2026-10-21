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
                output.WriteLine($"listening={listener.Listening} level={f.Level:0.00} beats={f.Beats} bands={string.Join("", f.Bands.Select(b => " .:-=+*#%@"[Math.Clamp((int)(b * 9.99), 0, 9)]))}");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        Assert.True(listener.Listening);
    }
}
