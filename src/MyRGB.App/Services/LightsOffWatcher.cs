using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using MyRGB.Core;

namespace MyRGB.App.Services;

/// <summary>
/// Holds the lights off while Windows is locked or asleep, and during night hours, if the user asked
/// for that. Each hold starts when its moment begins (the lock, the sleep, the start of the night) and
/// ends when it's over; turning the lights on in between ends it early, and it isn't put back until
/// that moment comes round again. The saved on/off choice is never touched.
/// </summary>
internal sealed class LightsOffWatcher : IDisposable
{
    public const string LockedId = "locked", AsleepId = "asleep", NightId = "night";

    private readonly AppHost _host;
    private readonly Func<bool> _locked;
    private readonly Func<DateTime> _now;
    private readonly ILogger _log;
    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private bool _wasLocked, _wasNight;
    private IntPtr _powerHandle;
    private static PowerCallback? s_callback; // kept alive for as long as Windows may call it

    public LightsOffWatcher(AppHost host, Func<bool>? locked, Func<DateTime>? now, bool watchPower, ILogger log)
    {
        _host = host;
        _locked = locked ?? SessionLocked;
        _now = now ?? (() => DateTime.Now);
        _log = log;
        if (watchPower) WatchPower();
        _timer = new System.Threading.Timer(_ => Check(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));
    }

    /// <summary>Look again now, e.g. right after the settings changed.</summary>
    public void Check()
    {
        try
        {
            lock (_gate)
            {
                var s = _host.Settings;
                var lights = _host.Lights;

                bool locked = s.OffWhenLocked && _locked();
                if (locked && !_wasLocked) lights.Hold(LockedId, "Windows is locked");
                else if (!locked && _wasLocked) lights.Release(LockedId);
                _wasLocked = locked;

                var night = NightFor(s);
                bool isNight = s.NightOn && night.Contains(TimeOnly.FromDateTime(_now()));
                string why = $"night hours, until {night.To:HH:mm}";
                if (isNight && !_wasNight) lights.Hold(NightId, why);
                else if (isNight && lights.IsHeld(NightId)) lights.Hold(NightId, why); // the end time was moved
                else if (!isNight && _wasNight) lights.Release(NightId);
                _wasNight = isNight;

                if (!s.OffWhenLocked) lights.Release(AsleepId);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Checking lock and night hours failed");
        }
    }

    public static NightHours NightFor(AppSettings s) =>
        new(NightHours.Parse(s.NightFrom, new TimeOnly(23, 0)), NightHours.Parse(s.NightTo, new TimeOnly(7, 0)));

    public void Dispose()
    {
        _timer.Dispose();
        if (_powerHandle != IntPtr.Zero)
        {
            try { PowerUnregisterSuspendResumeNotification(_powerHandle); }
            catch (Exception) { /* shutting down anyway */ }
            _powerHandle = IntPtr.Zero;
        }
    }

    private void OnPower(uint type)
    {
        if (!_host.Settings.OffWhenLocked) return;
        if (type == PbtApmSuspend)
        {
            _host.Lights.Hold(AsleepId, "the PC is asleep");
            _host.Engine.SkipFade(); // no time for a fade: Windows is about to cut power
            // Windows waits for this callback; give the render thread a couple of frames to send black.
            Thread.Sleep(150);
        }
        else if (type is PbtApmResumeAutomatic or PbtApmResumeSuspend)
        {
            _host.Lights.Release(AsleepId);
        }
    }

    // ---- Windows plumbing -------------------------------------------------------------------------

    private const uint PbtApmSuspend = 4, PbtApmResumeSuspend = 7, PbtApmResumeAutomatic = 18;
    private const uint DeviceNotifyCallback = 2;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint PowerCallback(IntPtr context, uint type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceNotifySubscribeParameters
    {
        public IntPtr Callback;
        public IntPtr Context;
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerRegisterSuspendResumeNotification(uint flags, ref DeviceNotifySubscribeParameters recipient, out IntPtr handle);

    [DllImport("powrprof.dll")]
    private static extern uint PowerUnregisterSuspendResumeNotification(IntPtr handle);

    private void WatchPower()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            s_callback = (_, type, _) =>
            {
                try { OnPower(type); }
                catch (Exception ex) { _log.LogWarning(ex, "Handling sleep or wake failed"); }
                return 0;
            };
            var p = new DeviceNotifySubscribeParameters { Callback = Marshal.GetFunctionPointerForDelegate(s_callback) };
            uint err = PowerRegisterSuspendResumeNotification(DeviceNotifyCallback, ref p, out _powerHandle);
            if (err != 0) _log.LogWarning("Can't hear about sleep and wake (error {Error})", err);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Can't hear about sleep and wake");
        }
    }

    private const int WtsSessionInfoEx = 25, WtsSessionStateLock = 0;

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    /// <summary>True while this Windows session is at the lock screen.</summary>
    public static bool SessionLocked()
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (!WTSQuerySessionInformationW(IntPtr.Zero, -1, WtsSessionInfoEx, out var buffer, out int bytes) || buffer == IntPtr.Zero)
            return false;
        try
        {
            // WTSINFOEXW: DWORD Level, then the union WTSINFOEX_LEVEL1_W { ULONG SessionId; WTS_CONNECTSTATE_CLASS
            // SessionState; LONG SessionFlags; ... }. The union holds LARGE_INTEGERs, so it starts at 8, not 4:
            // SessionId at 8, SessionState at 12, SessionFlags at 16 (checked against a live session).
            if (bytes < 20 || Marshal.ReadInt32(buffer) != 1) return false;
            return Marshal.ReadInt32(buffer, 16) == WtsSessionStateLock;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }
}
