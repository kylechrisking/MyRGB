using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyRGB.App.Controls;

namespace MyRGB.App.ViewModels;

public sealed record WelcomeStep(string Kicker, string Title, string Body, Geometry Icon, string? ActionLabel = null, string? ActionPage = null)
{
    public bool HasAction => ActionLabel is not null;
}

/// <summary>The first-run tour: what MyRGB is and how its pieces fit, one idea per page.</summary>
public sealed partial class WelcomeViewModel(Action<string?> finish) : ObservableObject
{
    public IReadOnlyList<WelcomeStep> Steps { get; } =
    [
        new("WELCOME", "One app for all your RGB",
            "MyRGB lights every device you own, from any brand, together. Your keyboard, mouse, headset, RAM, fans and motherboard all follow the same look, so you don't need a different app for each brand.",
            NavIcons.Home),
        new("STEP 1", "MyRGB finds your devices",
            "When it starts, MyRGB looks for everything it can light. Some devices it talks to directly; the rest go through OpenRGB, which comes with MyRGB and runs out of sight. You'll see everything on the Home page, with battery levels for wireless gear.",
            NavIcons.Devices, "See my devices", "devices"),
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
            "Closing the window keeps MyRGB running in the system tray, so your lights stay on. Use the top bar for power and brightness, the tray icon for quick switches, or Stream Deck keys if you have one.",
            NavIcons.Settings),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Current), nameof(IsFirst), nameof(IsLast), nameof(NextLabel), nameof(Progress))]
    private int _index;

    public WelcomeStep Current => Steps[Index];
    public bool IsFirst => Index == 0;
    public bool IsLast => Index == Steps.Count - 1;
    public string NextLabel => IsLast ? "Get started" : "Next";
    public string Progress => $"{Index + 1} of {Steps.Count}";

    [RelayCommand]
    private void Next()
    {
        if (IsLast) finish(null);
        else Index++;
    }

    [RelayCommand]
    private void Back()
    {
        if (!IsFirst) Index--;
    }

    [RelayCommand] private void Skip() => finish(null);

    /// <summary>Close the tour and jump to the page this step talks about.</summary>
    [RelayCommand] private void Go() => finish(Current.ActionPage);
}
