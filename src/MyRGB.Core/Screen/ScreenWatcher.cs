using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace MyRGB.Core.Screen;

/// <summary>
/// Copies the main screen into <see cref="ScreenFeed"/> about 30 times a second, but only while an
/// the lights play an effect that reads it. The rest of the time it checks twice a second whether anyone wants it, and
/// doesn't touch the screen.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScreenWatcher : IDisposable
{
    private readonly ILogger _log;
    private readonly Thread _thread;
    private readonly Func<bool> _live;
    private volatile bool _running = true;

    /// <param name="live">True while the lights themselves play Match Screen. Previews of the effect on
    /// the presets page read the feed too, but don't count: they alone never start copying the screen.</param>
    public ScreenWatcher(Func<bool> live, ILogger<ScreenWatcher> log)
    {
        _live = live;
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "Screen sync", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    private void Run()
    {
        ScreenCapture? capture = null;
        string? lastProblem = null;
        var clock = Stopwatch.StartNew();
        while (_running)
        {
            double started = clock.Elapsed.TotalSeconds, interval = 1.0 / 30;
            try
            {
                if (!_live() || !ScreenFeed.Wanted(TimeSpan.FromSeconds(2)))
                {
                    // Nobody is following the screen: let go of the graphics card's copy too.
                    capture?.Dispose();
                    capture = null;
                    ScreenFeed.Publish(null);
                    interval = 0.5;
                }
                else
                {
                    capture ??= new ScreenCapture();
                    ScreenFeed.Publish(capture.Grab());
                    if (capture.GpuProblem != lastProblem)
                    {
                        lastProblem = capture.GpuProblem;
                        if (lastProblem is not null) _log.LogInformation("Screen sync is using the processor to copy the screen: {Reason}", lastProblem);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Copying the screen failed");
                capture?.Dispose();
                capture = null;
                interval = 1;
            }
            double wait = interval - (clock.Elapsed.TotalSeconds - started);
            if (wait > 0) Thread.Sleep(TimeSpan.FromSeconds(wait));
        }
        capture?.Dispose();
        ScreenFeed.Publish(null);
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}
