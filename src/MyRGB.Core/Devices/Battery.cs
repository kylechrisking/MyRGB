using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MyRGB.Core.Devices;

/// <param name="Percent">0..100.</param>
/// <param name="Estimated">True when the device only reports a voltage and the percentage is derived from it.</param>
public sealed record BatteryStatus(int Percent, bool Charging, bool Estimated = false);

/// <summary>The latest reading for one wireless device. <see cref="Status"/> is null while it's asleep or off.</summary>
public sealed record BatteryReading(string HardwareKey, string Name, BatteryStatus? Status, DateTime ReadAt);

/// <summary>One wireless device whose battery can be read.</summary>
public interface IBatterySource
{
    /// <summary>Matches <see cref="IRgbDevice.HardwareKey"/>, so the UI can show the battery on the right device.</summary>
    string HardwareKey { get; }

    string Name { get; }

    /// <summary>Null when the device is unreachable right now (off, asleep, out of range).</summary>
    Task<BatteryStatus?> ReadAsync(CancellationToken ct);
}

/// <summary>Finds battery-powered devices of one family (Razer, Logitech, ...).</summary>
public interface IBatteryProvider
{
    IReadOnlyList<IBatterySource> Discover();
}

/// <summary>Polls every battery source in the background and keeps the latest readings.</summary>
public sealed class BatteryMonitor : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RediscoverInterval = TimeSpan.FromMinutes(5);

    /// <summary>How long a good reading stands in for failed ones before the device counts as asleep.</summary>
    internal static readonly TimeSpan HoldReading = TimeSpan.FromMinutes(3);

    private readonly IReadOnlyList<IBatteryProvider> _providers;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly HashSet<string> _loggedFailures = [];
    private readonly Dictionary<string, BatteryReading> _lastGood = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<IBatterySource> _sources = [];
    private DateTime _discoveredAt = DateTime.MinValue;
    private Task _loop = Task.CompletedTask;
    private volatile IReadOnlyDictionary<string, BatteryReading> _readings = new Dictionary<string, BatteryReading>();

    public BatteryMonitor(IEnumerable<IBatteryProvider> providers, ILogger<BatteryMonitor>? log = null)
    {
        _providers = providers.ToList();
        _log = log ?? NullLogger<BatteryMonitor>.Instance;
    }

    /// <summary>Latest reading per hardware key.</summary>
    public IReadOnlyDictionary<string, BatteryReading> Readings => _readings;

    /// <summary>Raised (any thread) after a poll changes any reading.</summary>
    public event EventHandler? Changed;

    public void Start() => _loop = Task.Run(() => RunAsync(_cts.Token));

    /// <summary>Re-find devices and read them now, e.g. after a rescan.</summary>
    public Task RefreshAsync()
    {
        _discoveredAt = DateTime.MinValue;
        return PollAsync(_cts.Token);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await PollAsync(ct);
            try { await Task.Delay(PollInterval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        if (!await _pollLock.WaitAsync(0, ct)) return; // a poll is already running
        try
        {
            if (DateTime.UtcNow - _discoveredAt > RediscoverInterval)
            {
                _sources = _providers.SelectMany(Discover).ToList();
                _discoveredAt = DateTime.UtcNow;
            }

            var next = new Dictionary<string, BatteryReading>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in _sources)
            {
                BatteryStatus? status = null;
                try
                {
                    status = await source.ReadAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (_loggedFailures.Add(source.HardwareKey))
                        _log.LogWarning(ex, "Reading the battery of {Device} failed", source.Name);
                }
                var reading = new BatteryReading(source.HardwareKey, source.Name, status, DateTime.UtcNow);
                // Wireless reads miss now and then (the radio is busy, other software is talking to
                // the device), so a failed poll soon after a good one doesn't make it "asleep" yet.
                if (status is not null) _lastGood[source.HardwareKey] = reading;
                else if (_lastGood.TryGetValue(source.HardwareKey, out var good) && reading.ReadAt - good.ReadAt < HoldReading) reading = good;
                next[source.HardwareKey] = reading;
            }

            bool changed = next.Count != _readings.Count
                || next.Any(kv => !_readings.TryGetValue(kv.Key, out var old) || old.Status != kv.Value.Status);
            _readings = next;
            if (changed) Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _pollLock.Release();
        }
    }

    private IEnumerable<IBatterySource> Discover(IBatteryProvider provider)
    {
        try { return provider.Discover(); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Battery discovery failed in {Provider}", provider.GetType().Name);
            return [];
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _loop; } catch { }
        _cts.Dispose();
    }
}
