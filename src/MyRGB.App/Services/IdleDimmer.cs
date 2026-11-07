using System.Runtime.InteropServices;

namespace MyRGB.App.Services;

/// <summary>
/// Dims the lights after a stretch without input and brings them straight back the moment you
/// touch anything. The dimmed value is transient: the brightness you actually chose is what
/// gets remembered, and it comes back on exit too.
/// </summary>
internal sealed class IdleDimmer
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint Size;
        public uint Tick;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    private readonly AppHost _host;
    private readonly System.Threading.Timer _timer;
    private bool _dimmed;
    private double _preDim = 1;

    public IdleDimmer(AppHost host)
    {
        _host = host;
        _timer = new System.Threading.Timer(_ => Check(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20));
    }

    /// <summary>The brightness to remember while dimmed, so saving settings keeps the user's choice.</summary>
    public double? HeldBrightness { get; private set; }

    /// <summary>Time since the last keyboard or mouse input.</summary>
    public static TimeSpan Idle()
    {
        if (!OperatingSystem.IsWindows()) return TimeSpan.Zero;
        var info = new LASTINPUTINFO { Size = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        int tick = unchecked((int)info.Tick);
        return TimeSpan.FromMilliseconds(Environment.TickCount - tick);
    }

    public void Dispose() => _timer.Dispose();

    private void Check()
    {
        try
        {
            var s = _host.Settings;
            if (!s.IdleDimOn)
            {
                Undim();
                return;
            }
            var idle = Idle();
            var after = TimeSpan.FromMinutes(Math.Clamp(s.IdleDimAfterMinutes, 1, 120));
            if (!_dimmed && idle >= after && _host.Lights.State.Enabled)
            {
                _preDim = _host.Engine.Brightness;
                HeldBrightness = _preDim;
                _host.Lights.SetBrightness(Math.Clamp(s.IdleDimPercent, 0, 100) / 100);
                _dimmed = true;
            }
            else if (_dimmed && idle < after)
            {
                Undim();
            }
        }
        catch (Exception) { /* a missed tick is fine; the next one tries again */ }
    }

    private void Undim()
    {
        if (!_dimmed) return;
        _dimmed = false;
        HeldBrightness = null;
        _host.Lights.SetBrightness(_preDim);
    }
}
