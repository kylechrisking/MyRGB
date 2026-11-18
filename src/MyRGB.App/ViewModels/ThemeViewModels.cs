using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyRGB.App.Services;

namespace MyRGB.App.ViewModels;

/// <summary>One card in Settings' Appearance picker, with a little drawing of the theme on it.</summary>
public sealed partial class ThemeOptionViewModel : ObservableObject
{
    private readonly Action<string> _pick;

    public ThemeOptionViewModel(ThemeInfo theme, Action<string> pick)
        : this(theme.Id, theme.Name, theme.Description, Brush.Parse(theme.Preview[0]), theme.Preview[1], theme.Preview[2], theme.Preview[3], pick) { }

    private ThemeOptionViewModel(string id, string name, string description, IBrush page, string surface, string ink, string accent, Action<string> pick)
    {
        Id = id;
        Name = name;
        Description = description;
        Page = page;
        Surface = Brush.Parse(surface);
        Ink = Brush.Parse(ink);
        Accent = Brush.Parse(accent);
        Edge = new SolidColorBrush(Color.Parse(ink), 0.25);
        _pick = pick;
    }

    /// <summary>"Match Windows": its thumbnail is half Notebook paper, half Chalkboard slate.</summary>
    public static ThemeOptionViewModel Auto(Action<string> pick)
    {
        var light = ThemeManager.Find(ThemeManager.AutoLight).Preview[0];
        var dark = ThemeManager.Find(ThemeManager.AutoDark).Preview[0];
        var split = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse(light), 0), new GradientStop(Color.Parse(light), 0.5),
                new GradientStop(Color.Parse(dark), 0.5), new GradientStop(Color.Parse(dark), 1),
            },
        };
        return new(ThemeManager.AutoId, "Match Windows", "Notebook by day, Chalkboard when Windows goes dark.",
            split, "#FBF8F0", "#22201C", "#F2D466", pick);
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public IBrush Page { get; }
    public IBrush Surface { get; }
    public IBrush Ink { get; }
    public IBrush Accent { get; }
    public IBrush Edge { get; }

    [ObservableProperty] private bool _isSelected;

    [RelayCommand]
    private void Pick() => _pick(Id);
}
