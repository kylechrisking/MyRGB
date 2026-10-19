using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyRGB.App.Services;
using MyRGB.Core.Devices;
using MyRGB.Core.Vendors;

namespace MyRGB.App.ViewModels;

public enum Readiness { Ready, Caution, Unknown }

/// <summary>
/// The "Replace vendor apps" page: which lighting apps are installed, whether MyRGB has taken over
/// everything they were doing for lighting, what else they do, and a way to uninstall them.
/// </summary>
public sealed partial class VendorAppsViewModel(AppHost host) : ObservableObject
{
    public ObservableCollection<VendorAppViewModel> Apps { get; } = [];

    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] private bool _checkedOnce;

    public bool NothingFound => CheckedOnce && Apps.Count == 0;
    public int RunningCount => Apps.Count(a => a.Running);

    /// <summary>Raised after each check, so Home can update its alerts.</summary>
    public event Action? Updated;

    [RelayCommand]
    public async Task Check()
    {
        if (IsChecking) return;
        IsChecking = true;
        try
        {
            var found = await Task.Run(host.DetectVendorApps);
            var devices = host.Devices.Devices;
            var names = DeviceNames.Unique(devices);
            var excluded = host.Settings.ExcludedDevices.ToHashSet(StringComparer.OrdinalIgnoreCase);

            Apps.Clear();
            foreach (var app in found)
                Apps.Add(new VendorAppViewModel(app, devices, names, excluded, this));
            CheckedOnce = true;
            OnPropertyChanged(nameof(NothingFound));
            OnPropertyChanged(nameof(RunningCount));
            Updated?.Invoke();
        }
        finally
        {
            IsChecking = false;
        }
    }

    /// <summary>Check again a little after an uninstaller was started, and keep checking for a while.</summary>
    internal void WatchForUninstall()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        int ticks = 0;
        timer.Tick += async (_, _) =>
        {
            if (++ticks > 30) timer.Stop();
            await Check();
        };
        timer.Start();
    }
}

public sealed partial class VendorAppViewModel : ObservableObject
{
    private readonly DetectedVendorApp _found;
    private readonly VendorAppsViewModel _owner;

    public VendorAppViewModel(
        DetectedVendorApp found, IReadOnlyList<IRgbDevice> devices, IReadOnlyDictionary<string, string> names,
        IReadOnlySet<string> excluded, VendorAppsViewModel owner)
    {
        _found = found;
        _owner = owner;
        var app = found.App;

        var theirs = app.Vendor is null ? [] : devices.Where(d => d.Vendor.Contains(app.Vendor, StringComparison.OrdinalIgnoreCase)).ToList();
        var notControlled = theirs.Where(d => excluded.Contains(d.Id) || d.Leds.Count == 0).ToList();
        ControlledDevices = theirs.Except(notControlled).Select(d => names.GetValueOrDefault(d.Id, d.Name)).ToList();

        (Readiness, Verdict) = (app.Vendor, theirs.Count, notControlled.Count) switch
        {
            (null, _, _) => (Readiness.Ready, "MyRGB does everything this app was doing for your lighting."),
            (_, 0, _) => (Readiness.Unknown,
                $"MyRGB isn't controlling any {app.Vendor} devices. If you have {app.Vendor} RGB gear that isn't on the Devices page, keep {app.Name} until it shows up."),
            (_, _, > 0) => (Readiness.Caution,
                $"{notControlled.Count} of your {app.Vendor} devices aren't controlled by MyRGB yet: {string.Join(", ", notControlled.Select(d => names.GetValueOrDefault(d.Id, d.Name)))}. Set them up on the Devices page first."),
            _ => (Readiness.Ready,
                theirs.Count == 1 ? $"MyRGB controls your {app.Vendor} device." : $"MyRGB controls all {theirs.Count} of your {app.Vendor} devices."),
        };
    }

    public string Name => _found.App.Name;
    public string Version => _found.Program.Version is { } v ? $"Version {v}" : "";
    public bool Running => _found.Running;
    public IReadOnlyList<string> YouWouldLose => _found.App.YouWouldLose;
    public bool HasLosses => YouWouldLose.Count > 0;
    public string? Tip => _found.App.Tip;
    public bool HasTip => !string.IsNullOrEmpty(Tip);
    public IReadOnlyList<string> ControlledDevices { get; }
    public bool HasControlledDevices => ControlledDevices.Count > 0;
    public IReadOnlyList<ComponentViewModel> Components => _found.Components.Select(c => new ComponentViewModel(c, this)).ToList();
    public bool HasComponents => _found.Components.Count > 0;
    public bool CanUninstall => VendorAppCatalog.UninstallCommand(_found.Program) is not null;

    public Readiness Readiness { get; }
    public string Verdict { get; }

    public string VerdictTitle => Readiness switch
    {
        Readiness.Ready => HasLosses ? "Safe to uninstall for lighting" : "Safe to uninstall",
        Readiness.Caution => "Not yet",
        _ => "Check first",
    };

    public IBrush VerdictBrush => new SolidColorBrush(Readiness switch
    {
        Readiness.Ready => Color.FromRgb(80, 220, 140),
        Readiness.Caution => Color.FromRgb(255, 190, 70),
        _ => Color.FromRgb(138, 143, 168),
    });

    public string RunningText => Running
        ? "Running now. While it runs it can override MyRGB's lighting on its devices."
        : "Installed, not running.";

    [ObservableProperty] private bool _confirming;
    [ObservableProperty] private string _status = "";

    [RelayCommand] private void AskUninstall() => Confirming = true;
    [RelayCommand] private void CancelUninstall() => Confirming = false;

    [RelayCommand]
    private void Uninstall()
    {
        Confirming = false;
        Launch(_found.Program);
    }

    internal void Launch(InstalledProgram program)
    {
        try
        {
            Status = InstalledPrograms.LaunchUninstaller(program)
                ? $"{program.Name}'s uninstaller is open. Follow its steps; this page updates when it's gone."
                : $"{program.Name} doesn't register an uninstaller. Remove it from Windows Settings › Apps.";
            _owner.WatchForUninstall();
        }
        catch (Exception ex)
        {
            Status = $"Couldn't start the uninstaller: {ex.Message}";
        }
    }
}

public sealed partial class ComponentViewModel(InstalledProgram program, VendorAppViewModel owner)
{
    public string Name => program.Name;
    public bool CanUninstall => VendorAppCatalog.UninstallCommand(program) is not null;

    [RelayCommand] private void Uninstall() => owner.Launch(program);
}
