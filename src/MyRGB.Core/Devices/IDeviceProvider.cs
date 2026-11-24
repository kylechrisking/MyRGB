namespace MyRGB.Core.Devices;

public enum ProviderStatus { Stopped, Connecting, Connected, Error }

/// <summary>
/// A source of devices. Each transport is one provider: the OpenRGB SDK client is the first,
/// and native drivers (Razer, Corsair, ...) are added later as their own providers.
/// </summary>
/// <remarks>
/// To add a device family: implement this interface plus <see cref="IRgbDevice"/>, then register
/// the provider with <see cref="DeviceManager"/>. Nothing else in the app needs to change.
/// </remarks>
public interface IDeviceProvider : IAsyncDisposable
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>When two providers report the same hardware, the higher priority wins (native beats OpenRGB).</summary>
    int Priority { get; }

    ProviderStatus Status { get; }
    string? StatusMessage { get; }

    /// <summary>Raised when the provider's device set changes (hot-plug, server restart, ...).</summary>
    event EventHandler? DevicesChanged;

    /// <summary>Connect if needed and return the devices currently available, already switched to direct control.</summary>
    Task<IReadOnlyList<IRgbDevice>> DiscoverAsync(CancellationToken ct = default);
}

/// <summary>
/// A provider whose own backend scans hardware once and keeps that list (OpenRGB): asked to look again
/// when the user presses Scan again, so a device switched on later can turn up without a restart.
/// </summary>
public interface IHardwareRescan
{
    /// <summary>Start the backend's own scan. Returns once asked; the provider raises DevicesChanged when it's done.</summary>
    Task RescanHardwareAsync(CancellationToken ct = default);
}
