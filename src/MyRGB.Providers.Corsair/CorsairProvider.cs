using MyRGB.Hid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyRGB.Core.Devices;

namespace MyRGB.Providers.Corsair;

/// <summary>
/// Native Corsair devices over HID, no iCUE or OpenRGB needed. Today: the Vanguard Pro 96.
/// Ranks above OpenRGB, so it wins if both ever see the same hardware.
/// </summary>
public sealed class CorsairProvider : IDeviceProvider
{
    public const string ProviderId = "corsair";

    /// <summary>The Vanguard Pro 96's command interface: interface 2, vendor usage page 0xFF42, usage 1.</summary>
    private const string VanguardPath = "vid_1b1c&pid_2b0e&mi_02";
    private const ushort VendorUsagePage = 0xFF42;

    /// <summary>How often to look for the keyboard while MyRGB doesn't have it.</summary>
    internal static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(5);

    /// <summary>Longest wait between attempts on a keyboard that is there but won't answer.</summary>
    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);

    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<HidInterface?> _find;
    private readonly Func<DateTime> _now;
    private CorsairKeyboard? _keyboard;
    private Timer? _watch;
    private int _rescanAsked;
    private int _failures;
    private DateTime _retryAfter;

    private readonly string? _screenDataDirectory;

    /// <param name="screenDataDirectory">Where the keyboard keeps the backup of what its screen had before MyRGB's picture; null turns screen uploads off.</param>
    public CorsairProvider(ILogger<CorsairProvider>? log = null, string? screenDataDirectory = null)
        : this(log, screenDataDirectory, FindKeyboard, () => DateTime.UtcNow, startWatch: true)
    {
    }

    internal CorsairProvider(ILogger? log, string? screenDataDirectory, Func<HidInterface?> find, Func<DateTime> now, bool startWatch)
    {
        _log = log ?? NullLogger<CorsairProvider>.Instance;
        _screenDataDirectory = screenDataDirectory;
        _find = find;
        _now = now;
        if (startWatch) _watch = new Timer(_ => CheckForKeyboard(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public string Id => ProviderId;
    public string DisplayName => "Corsair";
    public int Priority => 10;
    public ProviderStatus Status { get; private set; } = ProviderStatus.Stopped;
    public string? StatusMessage { get; private set; }

    public event EventHandler? DevicesChanged;

    private static HidInterface? FindKeyboard() =>
        HidDevices.Find(VanguardPath).FirstOrDefault(i => i.UsagePage == VendorUsagePage && i.OutputReportLength == Bragi.ReportLength);

    public async Task<IReadOnlyList<IRgbDevice>> DiscoverAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Volatile.Write(ref _rescanAsked, 0);
            // Nothing else tells MyRGB when a USB device comes back (after the PC's USB power-saving, a
            // KVM switch, a replug), so watch for it from the first scan on.
            _watch?.Change(WatchInterval, WatchInterval);

            if (_keyboard is { Faulted: true })
            {
                await _keyboard.DisposeAsync();
                _keyboard = null;
            }

            if (_keyboard is null)
            {
                var iface = await Task.Run(_find, ct);
                if (iface is null)
                {
                    Set(ProviderStatus.Connected, "no devices");
                    return [];
                }
                try
                {
                    _keyboard = await CorsairKeyboard.OpenAsync(iface, _log, ct);
                    _keyboard.ScreenDataDirectory = _screenDataDirectory;
                    _keyboard.Lost += () => DevicesChanged?.Invoke(this, EventArgs.Empty);
                    _failures = 0;
                    _log.LogInformation("Opened {Device} natively", _keyboard.Name);
                }
                catch (Exception ex) when (ex is IOException or TimeoutException)
                {
                    _failures++;
                    _retryAfter = _now() + RetryDelay(_failures);
                    MyRGB.Core.Disconnects.Warn(_log, ex, "Could not take control of the Corsair keyboard");
                    Set(ProviderStatus.Error, $"Keyboard found but not responding: {ex.Message}");
                    return [];
                }
            }

            Set(ProviderStatus.Connected, "1 device");
            return [_keyboard];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>5 s, 10 s, 20 s … up to <see cref="MaxRetryDelay"/>: a keyboard that won't answer isn't hammered.</summary>
    internal static TimeSpan RetryDelay(int failures) =>
        TimeSpan.FromTicks(Math.Min(MaxRetryDelay.Ticks, WatchInterval.Ticks << Math.Min(failures - 1, 16)));

    /// <summary>
    /// The watch: while MyRGB has no working keyboard and one is plugged in, ask for a rescan (once, until
    /// that scan runs). Returns true when it asked.
    /// </summary>
    internal bool CheckForKeyboard()
    {
        try
        {
            if (_keyboard is { Faulted: false }) return false;
            if (_now() < _retryAfter) return false;
            if (Volatile.Read(ref _rescanAsked) != 0) return false;
            if (_find() is null) return false;
            if (Interlocked.Exchange(ref _rescanAsked, 1) != 0) return false;
            _log.LogInformation("The Corsair keyboard is back; scanning");
            DevicesChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Looking for the Corsair keyboard failed");
            return false;
        }
    }

    private void Set(ProviderStatus status, string message)
    {
        Status = status;
        StatusMessage = message;
    }

    public async ValueTask DisposeAsync()
    {
        if (_watch is not null) await _watch.DisposeAsync();
        _watch = null;
        if (_keyboard is not null) await _keyboard.DisposeAsync();
        _keyboard = null;
        Set(ProviderStatus.Stopped, "");
    }
}
