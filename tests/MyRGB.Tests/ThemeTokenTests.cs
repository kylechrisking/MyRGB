using System.Text.RegularExpressions;

namespace MyRGB.Tests;

/// <summary>
/// Every theme has to define every token the window and the shared styles look up. A missing one
/// doesn't fail the build: the control just falls back to Fluent's default, which on a paper theme
/// means white text on cream. These read the XAML as text, so they don't need the app running.
/// </summary>
public partial class ThemeTokenTests
{
    private static readonly string Root = FindRoot();
    private static readonly string Themes = Path.Combine(Root, "src", "MyRGB.App", "Themes");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [GeneratedRegex(@"DynamicResource (\w+)")]
    private static partial Regex Used();

    [GeneratedRegex(@"x:Key=""(\w+)""")]
    private static partial Regex Defined();

    [GeneratedRegex(@"ResourceInclude Source=""avares://MyRGB/([\w/.]+)""")]
    private static partial Regex Include();

    [GeneratedRegex(@"avares://MyRGB/Themes/([\w.]+)\.axaml")]
    private static partial Regex Listed();

    private static HashSet<string> UsedKeys()
    {
        var files = new[] { Path.Combine(Root, "src", "MyRGB.App", "MainWindow.axaml") }
            .Concat(Directory.GetFiles(Themes, "*.axaml").Where(f => f.EndsWith("Shared.axaml") || f.EndsWith("Toggles.axaml") || f.EndsWith(".Styles.axaml")));
        return files.SelectMany(f => Used().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).ToHashSet();
    }

    /// <summary>The keys a theme file defines, following its ResourceIncludes (Defaults, the brand kit).</summary>
    private static HashSet<string> DefinedKeys(string file)
    {
        var text = File.ReadAllText(file);
        var keys = Defined().Matches(text).Select(m => m.Groups[1].Value).ToHashSet();
        foreach (Match m in Include().Matches(text))
        {
            var path = m.Groups[1].Value == "Theme.axaml"
                ? Path.Combine(Root, "branding", "Theme.axaml")
                : Path.Combine(Root, "src", "MyRGB.App", m.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar));
            keys.UnionWith(DefinedKeys(path));
        }
        return keys;
    }

    public static TheoryData<string> ThemeFiles()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.GetFiles(Path.Combine(FindRoot(), "src", "MyRGB.App", "Themes"), "*.axaml"))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            if (name is "Defaults" or "Shared" or "Toggles" || name.EndsWith(".Styles")) continue;
            data.Add(name);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ThemeFiles))]
    public void Theme_defines_every_token_the_window_uses(string theme)
    {
        var missing = UsedKeys().Except(DefinedKeys(Path.Combine(Themes, theme + ".axaml"))).Order().ToList();
        Assert.True(missing.Count == 0, $"{theme} is missing: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Every_theme_file_ThemeManager_names_exists()
    {
        var code = File.ReadAllText(Path.Combine(Root, "src", "MyRGB.App", "Services", "ThemeManager.cs"));
        var named = Listed().Matches(code).Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(named);
        Assert.All(named, n => Assert.True(File.Exists(Path.Combine(Themes, n + ".axaml")), $"{n}.axaml is missing"));
    }

    [Fact]
    public void Every_theme_file_is_offered_in_Settings()
    {
        var code = File.ReadAllText(Path.Combine(Root, "src", "MyRGB.App", "Services", "ThemeManager.cs"));
        var named = Listed().Matches(code).Select(m => m.Groups[1].Value).ToHashSet();
        Assert.All(ThemeFiles(), row => Assert.Contains(row.Data, named));
    }
}
