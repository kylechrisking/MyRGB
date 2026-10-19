using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MyRGB.Core.Devices;

/// <summary>Owns every provider and exposes the merged, de-duplicated device list.</summary>
public sealed class DeviceManager : IAsyncDisposable
{
    private readonly List<IDeviceProvider> _providers = [];
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly ILogger _log;
    private IReadOnlyList<IRgbDevice> _devices = [];

    public DeviceManager(ILogger<DeviceManager>? log = null) => _log = log ?? NullLogger<DeviceManager>.Instance;

    public IReadOnlyList<IDeviceProvider> Providers => _providers;
    public IReadOnlyList<IRgbDevice> Devices => _devices;

    /// <summary>Raised after <see cref="Devices"/> is replaced. May fire on any thread.</summary>
    public event EventHandler? DevicesChanged;

    public void AddProvider(IDeviceProvider provider)
    {
        _providers.Add(provider);
        provider.DevicesChanged += async (_, _) =>
        {
            try { await RefreshAsync(); }
            catch (Exception ex) { _log.LogWarning(ex, "Refresh after {Provider} change failed", provider.Id); }
        };
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            var found = new List<(IDeviceProvider Provider, IRgbDevice Device)>();
            foreach (var provider in _providers)
            {
                try
                {
                    foreach (var d in await provider.DiscoverAsync(ct))
                        found.Add((provider, d));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Provider {Provider} discovery failed", provider.Id);
                }
            }

            _devices = Merge(found);
            _log.LogInformation("{Count} devices available", _devices.Count);
        }
        finally
        {
            _refreshLock.Release();
        }
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    internal static IReadOnlyList<IRgbDevice> Merge(IEnumerable<(IDeviceProvider Provider, IRgbDevice Device)> found)
    {
        var result = new List<IRgbDevice>();
        var byHardware = new Dictionary<string, (int Priority, int Index)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (provider, device) in found)
        {
            if (device.HardwareKey is { } key && byHardware.TryGetValue(key, out var existing))
            {
                if (provider.Priority > existing.Priority)
                {
                    result[existing.Index] = device;
                    byHardware[key] = (provider.Priority, existing.Index);
                }
                continue;
            }
            if (device.HardwareKey is { } newKey)
                byHardware[newKey] = (provider.Priority, result.Count);
            result.Add(device);
        }
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var p in _providers)
        {
            try { await p.DisposeAsync(); }
            catch (Exception ex) { _log.LogWarning(ex, "Disposing {Provider} failed", p.Id); }
        }
    }
}
