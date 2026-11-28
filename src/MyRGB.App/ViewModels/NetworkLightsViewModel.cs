using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyRGB.App.Services;
using MyRGB.Providers.Network;

namespace MyRGB.App.ViewModels;

/// <summary>
/// "Lights on your network" on the Devices page: WLED lights the user adds by address or finds with a
/// short look around the network. Nothing on the network is touched until a light is added here.
/// </summary>
public sealed partial class NetworkLightsViewModel : ObservableObject
{
    private readonly AppHost _host;

    public NetworkLightsViewModel(AppHost host)
    {
        _host = host;
        foreach (var l in host.Settings.NetworkLights) Lights.Add(new NetworkLightRow(l, this));
        host.Devices.DevicesChanged += (_, _) => Dispatcher.UIThread.Post(RefreshStatus);
    }

    public ObservableCollection<NetworkLightRow> Lights { get; } = [];
    public ObservableCollection<FoundLightRow> Found { get; } = [];

    public bool HasLights => Lights.Count > 0;
    public bool HasFound => Found.Count > 0;

    [ObservableProperty] private string _newAddress = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;
    public bool IsIdle => !IsBusy;

    [RelayCommand]
    private async Task Add()
    {
        var address = NewAddress.Trim();
        if (address.Length == 0) { Note = "Type the light's address first, as shown in the WLED app."; return; }
        IsBusy = true;
        Note = $"Asking {address}…";
        try
        {
            var light = await LightFinder.CheckAsync(address);
            if (light is null)
            {
                Note = $"Nothing at {address} answered like a WLED light. Check the address in the WLED app, and that this PC is on the same network.";
                return;
            }
            AddLight(light);
            NewAddress = "";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task Find()
    {
        IsBusy = true;
        Found.Clear();
        OnPropertyChanged(nameof(HasFound));
        Note = "Looking for WLED lights on your network…";
        try
        {
            var found = await LightFinder.FindAsync(TimeSpan.FromSeconds(3));
            foreach (var f in found.Where(f => !Lights.Any(l => SameLight(l.Light.Address, f.Address))))
                Found.Add(new FoundLightRow(f, this));
            OnPropertyChanged(nameof(HasFound));
            Note = found.Count == 0
                ? "No WLED lights answered. If you know a light's address, type it in and press Add."
                : Found.Count == 0 ? "Found only lights you've already added." : "";
        }
        catch (Exception ex)
        {
            Note = $"Couldn't look around the network: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal void AddLight(FoundLight light)
    {
        if (Lights.Any(l => SameLight(l.Light.Address, light.Address)))
        {
            Note = $"{light.Name} is already added.";
            return;
        }
        Lights.Add(new NetworkLightRow(light.ToSetting(), this));
        foreach (var f in Found.Where(f => SameLight(f.Light.Address, light.Address)).ToList()) Found.Remove(f);
        OnPropertyChanged(nameof(HasLights));
        OnPropertyChanged(nameof(HasFound));
        Note = $"Added {light.Name} ({light.LedCount} LEDs). To put it where it sits in your room, turn on Arrange devices in the Studio.";
        Save();
    }

    internal void Remove(NetworkLightRow row)
    {
        Lights.Remove(row);
        OnPropertyChanged(nameof(HasLights));
        Note = $"Removed {row.Name}. It goes back to its own lighting in a few seconds.";
        Save();
    }

    private void Save() => _host.SetNetworkLights(Lights.Select(l => l.Light));

    private void RefreshStatus()
    {
        var provider = _host.Devices.Providers.OfType<NetworkLightsProvider>().FirstOrDefault();
        foreach (var l in Lights) l.Online = provider?.IsOnline(l.Light.Address) ?? false;
    }

    private static bool SameLight(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}

public sealed partial class NetworkLightRow(NetworkLight light, NetworkLightsViewModel owner) : ObservableObject
{
    public NetworkLight Light { get; } = light;
    public string Name => Light.Name ?? Light.Address;
    public string Address => Light.Address;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _online;

    public string StatusText => Online ? "Connected" : "Not answering";

    [RelayCommand]
    private void Remove() => owner.Remove(this);
}

public sealed partial class FoundLightRow(FoundLight light, NetworkLightsViewModel owner) : ObservableObject
{
    public FoundLight Light { get; } = light;
    public string Name => Light.Name;
    public string Detail => $"{Light.Address} · {Light.LedCount} LEDs";

    [RelayCommand]
    private void Add() => owner.AddLight(Light);
}
