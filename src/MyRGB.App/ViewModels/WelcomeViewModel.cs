using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyRGB.App.Controls;
using MyRGB.Core;
using MyRGB.Core.Localization;

namespace MyRGB.App.ViewModels;

public sealed record WelcomeStep(string Kicker, string Title, string Body, Geometry Icon, string? ActionLabel = null, string? ActionPage = null,
    bool IsCheck = false)
{
    public bool HasAction => ActionLabel is not null;
}

/// <summary>One color on the "check your devices" step.</summary>
public sealed partial class FlashSwatch(Rgb color, Action<FlashSwatch> pick) : ObservableObject
{
    public Rgb Color => color;
    public IBrush Brush { get; } = new SolidColorBrush(Avalonia.Media.Color.FromRgb(color.R, color.G, color.B));

    [ObservableProperty] private bool _isSelected;

    [RelayCommand] private void Pick() => pick(this);
}

/// <summary>
/// The first-run tour: what MyRGB is and how its pieces fit, one idea per page. One page checks
/// the hardware: every device pulses in a color you pick, and you say whether they all lit up.
/// </summary>
public sealed partial class WelcomeViewModel : ObservableObject
{
    private readonly Action<string?> _finish;
    private readonly Action<Rgb?> _flash;
    private readonly Func<int> _deviceCount;

    /// <param name="flash">Pulse every device in a color, or stop when given null.</param>
    public WelcomeViewModel(Action<string?> finish, Action<Rgb?>? flash = null, Func<int>? deviceCount = null)
    {
        _finish = finish;
        _flash = flash ?? (_ => { });
        _deviceCount = deviceCount ?? (() => 0);
        Swatches =
        [
            .. new[] { "#FF2020", "#FF8A00", "#FFE000", "#20FF40", "#00D8FF", "#2040FF", "#9A20FF", "#FF20D0" }
                .Select(hex => new FlashSwatch(Rgb.Parse(hex), Pick)),
        ];
        Swatches[3].IsSelected = true;
    }

    public IReadOnlyList<WelcomeStep> Steps { get; } =
    [
        new(Strings.Get("tour.welcome.kicker"), Strings.Get("tour.welcome.title"), Strings.Get("tour.welcome.body"), NavIcons.Home),
        new(Strings.Format("tour.step", 1), Strings.Get("tour.devices.title"), Strings.Get("tour.devices.body"), NavIcons.Devices, Strings.Get("tour.devices.action"), "devices"),
        new(Strings.Get("tour.check.kicker"), Strings.Get("tour.check.title"), Strings.Get("tour.check.body"), NavIcons.Flash, IsCheck: true),
        new(Strings.Format("tour.step", 2), Strings.Get("tour.canvas.title"), Strings.Get("tour.canvas.body"), NavIcons.Layout, Strings.Get("tour.canvas.action"), "layout"),
        new(Strings.Format("tour.step", 3), Strings.Get("tour.presets.title"), Strings.Get("tour.presets.body"), NavIcons.Presets, Strings.Get("tour.presets.action"), "presets"),
        new(Strings.Format("tour.step", 4), Strings.Get("tour.vendors.title"), Strings.Get("tour.vendors.body"), NavIcons.Vendors, Strings.Get("tour.vendors.action"), "vendors"),
        new(Strings.Format("tour.step", 5), Strings.Get("tour.monitoring.title"), Strings.Get("tour.monitoring.body"), NavIcons.Monitoring, Strings.Get("tour.monitoring.action"), "monitoring"),
        new(Strings.Format("tour.step", 6), Strings.Get("tour.anywhere.title"), Strings.Get("tour.anywhere.body"), NavIcons.Settings),
        new(Strings.Format("tour.step", 7), Strings.Get("tour.looks.title"), Strings.Get("tour.looks.body"), NavIcons.Settings, Strings.Get("tour.looks.action"), "settings"),
    ];

    public IReadOnlyList<FlashSwatch> Swatches { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Current), nameof(IsFirst), nameof(IsLast), nameof(NextLabel), nameof(Progress), nameof(FoundText))]
    private int _index;

    /// <summary>The answer on the check step: null until asked, then yes or no.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnsweredYes), nameof(AnsweredNo))]
    private bool? _allLit;

    public bool AnsweredYes => AllLit == true;
    public bool AnsweredNo => AllLit == false;

    public WelcomeStep Current => Steps[Index];
    public bool IsFirst => Index == 0;
    public bool IsLast => Index == Steps.Count - 1;
    public string NextLabel => Strings.Get(IsLast ? "tour.getStarted" : "tour.next");
    public string Progress => Strings.Format("tour.progress", Index + 1, Steps.Count);

    public string FoundText => _deviceCount() switch
    {
        0 => Strings.Get("tour.check.foundNone"),
        1 => Strings.Get("tour.check.foundOne"),
        int n => Strings.Format("tour.check.foundMany", n),
    };

    partial void OnIndexChanged(int value)
    {
        if (Current.IsCheck) _flash(Swatches.First(s => s.IsSelected).Color);
        else _flash(null);
    }

    private void Pick(FlashSwatch swatch)
    {
        foreach (var s in Swatches) s.IsSelected = ReferenceEquals(s, swatch);
        if (Current.IsCheck) _flash(swatch.Color);
    }

    [RelayCommand] private void Answer(string yes) => AllLit = yes == "yes";

    [RelayCommand]
    private void Next()
    {
        if (IsLast) Finish(null);
        else Index++;
    }

    [RelayCommand]
    private void Back()
    {
        if (!IsFirst) Index--;
    }

    [RelayCommand] private void Skip() => Finish(null);

    /// <summary>Close the tour and jump to the page this step talks about.</summary>
    [RelayCommand] private void Go(string? page) => Finish(page ?? Current.ActionPage);

    private void Finish(string? page)
    {
        _flash(null);
        _finish(page);
    }
}
