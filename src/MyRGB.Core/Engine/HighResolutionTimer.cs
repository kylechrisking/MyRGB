using System.Runtime.InteropServices;

namespace MyRGB.Core.Engine;

/// <summary>
/// Raises the Windows timer resolution to 1 ms while the render loop runs. Without it Thread.Sleep
/// rounds to ~15.6 ms and 30/60 fps animation stutters.
/// </summary>
internal static partial class HighResolutionTimer
{
    public static IDisposable Begin()
    {
        if (!OperatingSystem.IsWindows()) return new Scope(false);
        return new Scope(timeBeginPeriod(1) == 0);
    }

    [LibraryImport("winmm.dll")]
    private static partial uint timeBeginPeriod(uint period);

    [LibraryImport("winmm.dll")]
    private static partial uint timeEndPeriod(uint period);

    private sealed class Scope(bool active) : IDisposable
    {
        public void Dispose()
        {
            if (active) timeEndPeriod(1);
        }
    }
}
