using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyRGB.App.Controls;
using MyRGB.Core;

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
        new("WELCOME", "One app for all your RGB",
            "MyRGB lights every device you own, from any brand, together. Your keyboard, mouse, headset, RAM, fans and motherboard all follow the same look, so you don't need a different app for each brand.",
            NavIcons.Home),
        new("STEP 1", "MyRGB finds your devices",
            "When it starts, MyRGB looks for everything it can light. Some devices it talks to directly; the rest go through OpenRGB, which comes with MyRGB and runs out of sight. You'll see everything on the Home page, with battery levels for wireless gear.",
            NavIcons.Devices, "See my devices", "devices"),
        new("CHECK", "Are all your devices pulsing?",
            "MyRGB is pulsing everything it found in the color below. Pick one that's easy to see, then look around your desk and inside your PC.",
            NavIcons.Flash, IsCheck: true),
        new("STEP 2", "Effects paint one big canvas",
            "Think of your desk as one picture. On the Layout page, each device sits where it really is. An effect like Rainbow Wave then sweeps across all of them in one motion, from the keyboard to the mouse to the headset stand.",
            NavIcons.Layout, "Arrange my layout", "layout"),
        new("STEP 3", "Pick a preset, or make your own",
            "Presets are ready-made looks: click one to apply it. In the Studio you can change any effect's colors and speed and watch it change on your devices live. You can even give one device its own look, then save the result as your own preset.",
            NavIcons.Presets, "Browse presets", "presets"),
        new("STEP 4", "Retire the vendor apps",
            "iCUE, G HUB, Synapse and Armoury Crate all try to control lighting too, and they fight with MyRGB and with each other. The Vendor apps page shows which ones are installed, whether MyRGB has taken over their devices, and what else you'd lose by uninstalling them.",
            NavIcons.Vendors, "Review vendor apps", "vendors"),
        new("STEP 5", "Control it from anywhere",
            "Closing the window keeps MyRGB running in the system tray, so your lights stay on. Use the player bar for presets, the top bar for power and brightness, the tray icon for quick switches, or Stream Deck keys if you have one.",
            NavIcons.Settings),
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
    public string NextLabel => IsLast ? "Get started" : "Next";
    public string Progress => $"{Index + 1} of {Steps.Count}";

    public string FoundText => _deviceCount() switch
    {
        0 => "MyRGB hasn't found any devices yet. If it's still starting up, give it a few seconds.",
        1 => "MyRGB found 1 device.",
        int n => $"MyRGB found {n} devices.",
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
