using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.VisualTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace MyRGB.App.Services;

/// <summary>
/// Colors the drawn controls (charts, the layout stage, battery gauges) and status dots need from C#,
/// where a XAML resource can't reach. The "stage" is the dark well LEDs are drawn on: lights look like
/// lights on black, so even paper themes keep it dark, just tinted to match.
/// </summary>
public sealed record ThemePalette(
    Color Accent, Color Region, Color Stage, Color StageLine, Color StageLabel, Color StageLabelStrong,
    Color Empty, Color Success, Color Warning, Color Danger, Color Info, Color Muted);

/// <summary>One look MyRGB can wear. <see cref="Preview"/> is page, card, ink, accent, for the picker.</summary>
public sealed record ThemeInfo(
    string Id, string Name, string Description, bool Dark, string Resources, string? Styles, ThemePalette Palette, string[] Preview,
    double StageRadius = 10);

/// <summary>The themes, and switching between them while the app runs.</summary>
public static class ThemeManager
{
    public const string DefaultId = "notebook";

    public static IReadOnlyList<ThemeInfo> All { get; } =
    [
        new("notebook", "Notebook", "Cream ruled paper, ink and a handwritten note or two.", false,
            "avares://MyRGB/Themes/Notebook.axaml", "avares://MyRGB/Themes/Notebook.Styles.axaml",
            new(C(0x2F5FC4), C(0xF1EBDD), C(0x1E1C19), C(0x3A362F), C(0xC9C1B0), C(0xF1EBDD),
                C(0xE4DCCA), C(0x2A8A4E), C(0xB86E00), C(0xC8323C), C(0x2F6FD6), C(0x6E675C)),
            ["#F1EBDD", "#FBF8F0", "#22201C", "#2F5FC4"], StageRadius: 2),
        new("bench", "Bench", "A bare circuit board: copper, silkscreen, no curves.", true,
            "avares://MyRGB/Themes/Bench.axaml", "avares://MyRGB/Themes/Bench.Styles.axaml",
            new(C(0x3FE08A), C(0x0C0F0D), C(0x050605), C(0x3A413C), C(0xC5C2B8), C(0xE9E6DC),
                C(0x1A201C), C(0x3FE08A), C(0xFFB03A), C(0xFF4D5E), C(0x3A9BFF), C(0x8F948C)),
            ["#0C0F0D", "#13201A", "#E9E6DC", "#C98B4A"], StageRadius: 0),
        new("classic", "Classic", "Grey 3D buttons and Tahoma, like PCs used to be.", false,
            "avares://MyRGB/Themes/Classic.axaml", "avares://MyRGB/Themes/Classic.Styles.axaml",
            new(C(0x0B2A8A), C(0xC3C3C3), C(0x000000), C(0x404040), C(0xC0C0C0), C(0xFFFFFF),
                C(0xA0A0A0), C(0x008000), C(0xA06000), C(0xC00000), C(0x0000C0), C(0x404040)),
            ["#C3C3C3", "#FFFFFF", "#000000", "#0B2A8A"], StageRadius: 0),
        new("chalkboard", "Chalkboard", "Notebook after dark: chalk on slate, a wooden frame.", true,
            "avares://MyRGB/Themes/Chalkboard.axaml", "avares://MyRGB/Themes/Chalkboard.Styles.axaml",
            new(C(0xF2D466), C(0x2B332D), C(0x151A16), C(0x3A443C), C(0xCFCDC2), C(0xECEAE0),
                C(0x3A443C), C(0x9EE6A0), C(0xF2D466), C(0xF28B82), C(0x8EC5F5), C(0xA3AA9F)),
            ["#2B332D", "#313A33", "#ECEAE0", "#F2D466"], StageRadius: 3),
        new("blueprint", "Blueprint", "White linework on blueprint blue, with a grid.", true,
            "avares://MyRGB/Themes/Blueprint.axaml", "avares://MyRGB/Themes/Blueprint.Styles.axaml",
            new(C(0x9FD3FF), C(0x1C3B6B), C(0x0E1F3A), C(0x2A4E85), C(0xD3DFF2), C(0xF2F6FF),
                C(0x2A4E85), C(0x8AF0B0), C(0xFFD27A), C(0xFF8A8A), C(0x8AC8FF), C(0xA9BEDF)),
            ["#1C3B6B", "#1E4074", "#F2F6FF", "#9FD3FF"], StageRadius: 0),
        new("phosphor", "Phosphor", "A green-screen terminal. Everything in Consolas.", true,
            "avares://MyRGB/Themes/Phosphor.axaml", "avares://MyRGB/Themes/Phosphor.Styles.axaml",
            new(C(0x39FF7A), C(0x060C07), C(0x020503), C(0x0F3A1C), C(0x2FD866), C(0x39FF7A),
                C(0x0F2214), C(0x39FF7A), C(0xFFB000), C(0xFF5050), C(0x5AE0FF), C(0x22AE55)),
            ["#060C07", "#07100A", "#39FF7A", "#39FF7A"], StageRadius: 0),
        new("original", "Original", "The dark slate and green MyRGB started with.", true,
            "avares://MyRGB/Themes/Original.axaml", null,
            new(C(0x56DB8F), C(0x071117), C(0x040E13), C(0x223039), C(0xBCC7C7), C(0xE2EAE5),
                C(0x162127), C(0x56DB8F), C(0xFCB442), C(0xF75D59), C(0x4ABAF4), C(0x829298)),
            ["#071117", "#0D181E", "#E2EAE5", "#56DB8F"]),
    ];

    public static ThemeInfo Current { get; private set; } = All[0];
    public static ThemePalette Palette => Current.Palette;

    /// <summary>Raised after a switch, so drawn controls and status colors can repaint.</summary>
    public static event Action? Changed;

    private static StyleInclude? _styles;

    public static ThemeInfo Find(string? id) => All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public static void Apply(Application app, string? id)
    {
        var theme = Find(id);
        var baseUri = new Uri("avares://MyRGB/");

        var merged = app.Resources.MergedDictionaries;
        merged.Clear();
        merged.Add(new ResourceInclude(baseUri) { Source = new Uri(theme.Resources) });

        if (_styles is not null) app.Styles.Remove(_styles);
        _styles = null;
        if (theme.Styles is { } styles)
        {
            _styles = new StyleInclude(baseUri) { Source = new Uri(styles) };
            app.Styles.Add(_styles);
        }

        app.RequestedThemeVariant = theme.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        if (app.Styles.OfType<FluentTheme>().FirstOrDefault() is { } fluent)
            foreach (var palette in fluent.Palettes.Values)
            {
                palette.Accent = theme.Palette.Accent;
                palette.RegionColor = theme.Palette.Region;
            }

        Current = theme;
        // Charts, the LED stage and gauges draw their own colors; ask them to draw again.
        if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            foreach (var window in desktop.Windows)
                foreach (var visual in window.GetVisualDescendants())
                    visual.InvalidateVisual();
        Changed?.Invoke();
    }

    private static Color C(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
