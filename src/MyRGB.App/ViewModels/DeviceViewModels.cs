using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyRGB.App.Controls;
using MyRGB.App.Services;
using MyRGB.Core.Devices;

namespace MyRGB.App.ViewModels;

/// <summary>One device, as shown on Home (a tile) and on the Devices page (a row).</summary>
public sealed partial class DeviceRowViewModel(
    IRgbDevice device,
    string displayName,
    bool included,
    Action<string, bool> setIncluded,
    Func<string, string, int, Task> setZoneSize,
    Action<string>? flash = null,
    ScreenViewModel? screen = null) : ObservableObject
{
    /// <summary>Set for devices with a screen MyRGB can put a picture on.</summary>
    public ScreenViewModel? Screen => screen;
    public bool HasScreen => screen is not null;

    /// <summary>Only devices MyRGB is driving can be flashed.</summary>
    public bool CanFlash => flash is not null && Included && !NeedsSetup;

    [RelayCommand]
    private void Flash() => flash?.Invoke(device.Id);

    public string Id => device.Id;
    public string Name => displayName;
    public string Vendor => device.Vendor;
    public string Type => device.Slot is { } slot ? $"{DeviceIcons.Label(device.Type)} · DIMM {slot}" : DeviceIcons.Label(device.Type);
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
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(StatusBrush), nameof(CanFlash))]
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

/// <summary>A device's screen on the Devices page: the picture on it, and whether it shows.</summary>
public sealed partial class ScreenViewModel(IScreenDevice device, AppHost host) : ObservableObject
{
    private bool _reverting;

    public string SizeText => $"Screen · {device.ScreenWidth} × {device.ScreenHeight}";
    public bool HasImage => device.HasScreenImage;

    /// <summary>Shown in place of the preview; a picture stored without one (put there before this app version) still says so.</summary>
    public string PlaceholderText => HasImage ? "Picture stored" : "No picture";

    [ObservableProperty]
    private Bitmap? _preview = LoadPreview(host.ScreenPreviewPath(device));

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChooseImageCommand), nameof(RemoveImageCommand))]
    private bool _busy;

    [ObservableProperty]
    private string _status = "";

    /// <summary>On: the keyboard runs its own lighting so its screen shows. Saved, and applied again on every re-plug.</summary>
    [ObservableProperty]
    private bool _screenFirst = host.Settings.ScreenFirstDevices.Contains(device.Id, StringComparer.OrdinalIgnoreCase);

    public string ScreenFirstHint => "The screen only shows while the keyboard runs its own lighting, so while this is on MyRGB's colors don't reach it. Switch it off to have them back.";

    private bool NotBusy() => !Busy;

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task ChooseImage()
    {
        if (await PickImageAsync() is { } path) await UseImageAsync(path);
    }

    /// <summary>Fits the picture to the screen and puts it there. Call on the UI thread.</summary>
    public async Task UseImageAsync(string path)
    {
        Busy = true;
        Status = "Putting it on the screen…";
        try
        {
            byte[] rgb, png;
            using (var fitted = ScreenImage.Render(path, device.ScreenWidth, device.ScreenHeight))
            {
                rgb = ScreenImage.ToRgb(fitted);
                png = ScreenImage.ToPng(fitted);
            }
            await Task.Run(() => host.SetScreenImageAsync(device, rgb, png));
            Preview = new Bitmap(new MemoryStream(png));
            Status = ScreenFirst ? "It's on the screen." : "Stored on the keyboard. It shows while “Show the screen” is on, or whenever MyRGB is closed.";
        }
        catch (Exception ex)
        {
            Status = $"Couldn't put it on the screen: {ex.Message}";
        }
        finally
        {
            Busy = false;
            OnPropertyChanged(nameof(HasImage));
            OnPropertyChanged(nameof(PlaceholderText));
        }
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task RemoveImage()
    {
        Busy = true;
        Status = "Taking it off…";
        try
        {
            await Task.Run(() => host.ClearScreenImageAsync(device));
            Preview = null;
            Status = "Removed; the screen is back as it was.";
        }
        catch (Exception ex)
        {
            Status = $"Couldn't take it off: {ex.Message}";
        }
        finally
        {
            Busy = false;
            OnPropertyChanged(nameof(HasImage));
            OnPropertyChanged(nameof(PlaceholderText));
        }
    }

    partial void OnScreenFirstChanged(bool value)
    {
        if (_reverting) return;
        _ = ApplyScreenFirstAsync(value);
    }

    private async Task ApplyScreenFirstAsync(bool on)
    {
        try
        {
            await Task.Run(() => host.SetScreenFirstAsync(device, on));
            Status = on ? "The keyboard is running its own lighting, so the screen shows." : "MyRGB is lighting the keyboard again.";
        }
        catch (Exception ex)
        {
            Status = $"Couldn't switch: {ex.Message}";
            _reverting = true;
            ScreenFirst = !on;
            _reverting = false;
        }
    }

    private static Bitmap? LoadPreview(string path)
    {
        try { return File.Exists(path) ? new Bitmap(path) : null; }
        catch (Exception) { return null; }
    }

    private static async Task<string?> PickImageAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window }) return null;
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a picture for the keyboard's screen",
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }
}
