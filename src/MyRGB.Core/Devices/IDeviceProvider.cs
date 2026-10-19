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
