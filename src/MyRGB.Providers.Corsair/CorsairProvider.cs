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

    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CorsairKeyboard? _keyboard;

    public CorsairProvider(ILogger<CorsairProvider>? log = null) => _log = log ?? NullLogger<CorsairProvider>.Instance;

    public string Id => ProviderId;
    public string DisplayName => "Corsair";
    public int Priority => 10;
    public ProviderStatus Status { get; private set; } = ProviderStatus.Stopped;
    public string? StatusMessage { get; private set; }

    public event EventHandler? DevicesChanged;

    public async Task<IReadOnlyList<IRgbDevice>> DiscoverAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_keyboard is { Faulted: true })
            {
                await _keyboard.DisposeAsync();
                _keyboard = null;
            }

            if (_keyboard is null)
            {
                var iface = await Task.Run(() => HidDevices.Find(VanguardPath).FirstOrDefault(i => i.UsagePage == VendorUsagePage && i.OutputReportLength == Bragi.ReportLength), ct);
                if (iface is null)
                {
                    Set(ProviderStatus.Connected, "no devices");
                    return [];
                }
                try
                {
                    _keyboard = await CorsairKeyboard.OpenAsync(iface, _log, ct);
                    _keyboard.Lost += () => DevicesChanged?.Invoke(this, EventArgs.Empty);
                    _log.LogInformation("Opened {Device} natively", _keyboard.Name);
                }
                catch (Exception ex) when (ex is IOException or TimeoutException)
                {
                    _log.LogWarning(ex, "Could not take control of the Corsair keyboard");
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

    private void Set(ProviderStatus status, string message)
    {
        Status = status;
        StatusMessage = message;
    }

    public async ValueTask DisposeAsync()
    {
        if (_keyboard is not null) await _keyboard.DisposeAsync();
        _keyboard = null;
        Set(ProviderStatus.Stopped, "");
    }
}
