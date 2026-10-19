using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyRGB.App.Controls;
using MyRGB.Core.Devices;

namespace MyRGB.App.ViewModels;

/// <summary>One device, as shown on Home (a tile) and on the Devices page (a row).</summary>
public sealed partial class DeviceRowViewModel(
    IRgbDevice device,
    string displayName,
    bool included,
    Action<string, bool> setIncluded,
    Func<string, string, int, Task> setZoneSize) : ObservableObject
{
    public string Id => device.Id;
    public string Name => displayName;
    public string Vendor => device.Vendor;
    public string Type => DeviceIcons.Label(device.Type);
    public Geometry Icon => DeviceIcons.For(device.Type);
    public string? HardwareKey => device.HardwareKey;
    public bool NeedsSetup => device.Leds.Count == 0;

    public string LedBadge => device.Leds.Count == 1 ? "1 LED" : $"{device.Leds.Count} LEDs";

    /// <summary>How MyRGB reaches the device, in words.</summary>
    public string Connection => device.ProviderId == "openrgb" ? "Through OpenRGB" : "MyRGB native driver";

    /// <summary>Zone names are only worth a line when there's more than one, or setup is needed.</summary>
    public bool ShowZones => NeedsSetup || device.Zones.Count > 1;

    public string Zones => NeedsSetup
        ? "Tell MyRGB how many LEDs are on each channel below, then Apply."
        : string.Join(" · ", device.Zones.Select(z => z.Name));

    public IReadOnlyList<ZoneSizeViewModel> ResizableZones { get; } = device is IResizableDevice r
        ? r.ResizableZones.Select(z => new ZoneSizeViewModel(device.Id, z, setZoneSize)).ToArray()
        : [];
    public bool HasResizableZones => ResizableZones.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(StatusBrush))]
    private bool _included = included;

    partial void OnIncludedChanged(bool value) => setIncluded(device.Id, value);

    public string StatusText => NeedsSetup ? "Needs setup" : Included ? "Controlled by MyRGB" : "Left to other software";

    public IBrush StatusBrush => new SolidColorBrush(NeedsSetup ? Color.FromRgb(252, 180, 66)
        : Included ? Color.FromRgb(86, 219, 143) : Color.FromRgb(130, 146, 152));

    // ---- Battery (wireless devices only) ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBattery), nameof(BatteryText), nameof(BatteryTip))]
    private BatteryStatus? _battery;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBattery), nameof(BatteryText), nameof(BatteryTip))]
    private bool _batteryAsleep;

    public bool HasBattery => Battery is not null || BatteryAsleep;
    public int BatteryPercent => Battery?.Percent ?? 0;
    public bool BatteryCharging => Battery?.Charging ?? false;
    public string BatteryText => Battery is { } b ? $"{b.Percent}%" : BatteryAsleep ? "asleep" : "";

    public string BatteryTip => Battery is { } b
        ? $"Battery {b.Percent}%{(b.Charging ? ", charging" : "")}{(b.Estimated ? " (estimated from voltage)" : "")}"
        : "The device is off or asleep, so its battery can't be read right now.";

    public void ApplyBattery(BatteryReading? reading)
    {
        Battery = reading?.Status;
        BatteryAsleep = reading is { Status: null };
        OnPropertyChanged(nameof(BatteryPercent));
        OnPropertyChanged(nameof(BatteryCharging));
    }
}

/// <summary>One user-sized zone (fan channel, ARGB header) on the Devices page.</summary>
public sealed partial class ZoneSizeViewModel(string deviceId, ResizableZone zone, Func<string, string, int, Task> setZoneSize) : ObservableObject
{
    public string Name => zone.Name;
    public decimal Min => zone.Min;
    public decimal Max => zone.Max;
    public string Range => $"LEDs ({zone.Min}–{zone.Max})";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private decimal? _ledCount = zone.LedCount;

    private bool CanApply() => LedCount is { } n && (int)n != zone.LedCount;

    // Applying rescans devices, which rebuilds this row with the new size.
    [RelayCommand(CanExecute = nameof(CanApply))]
    private Task Apply() => Task.Run(() => setZoneSize(deviceId, zone.Name, (int)LedCount!.Value));
}

/// <summary>A wireless device with a battery that MyRGB doesn't light (e.g. a mouse without RGB).</summary>
public sealed record BatteryOnlyViewModel(string Name, BatteryStatus? Status)
{
    public bool HasStatus => Status is not null;
    public int Percent => Status?.Percent ?? 0;
    public bool Charging => Status?.Charging ?? false;
    public string Text => Status is { } s ? $"{s.Percent}%{(s.Charging ? " · charging" : "")}" : "asleep";
}
