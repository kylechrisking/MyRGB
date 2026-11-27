using System.Text.RegularExpressions;
using MyRGB.Core.Localization;

namespace MyRGB.Tests;

/// <summary>
/// The words on screen come from lang/*.json. A key the window asks for that English lacks shows as
/// the bare key, and a translation with a different {0} than English breaks the sentence, so both
/// are checked here rather than spotted on screen.
/// </summary>
public sealed partial class StringsTests : IDisposable
{
    private static readonly string Root = FindRoot();
    private readonly string _folder = Directory.CreateTempSubdirectory("myrgb-lang-").FullName;

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    public void Dispose()
    {
        Strings.Use(Strings.Source);
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void EnglishIsBuiltInAndListedFirst()
    {
        Strings.Use(Strings.Source);
        Assert.Equal("How it works", Strings.Get("top.howItWorks"));
        Assert.Equal((Strings.Source, "English"), Strings.Available()[0]);
    }

    [Fact]
    public void AMissingKeyShowsAsItself() =>
        Assert.Equal("no.such.key", Strings.Get("no.such.key"));

    [Fact]
    public void FormatFillsInTheValue()
    {
        Strings.Use(Strings.Source);
        Assert.Equal("MyRGB 1.0.0 is available", Strings.Format("update.available", "1.0.0"));
    }

    [Fact]
    public void AUserFileIsUsedAndFallsBackToEnglish()
    {
        File.WriteAllText(Path.Combine(_folder, "xx.json"), """{ "_language": "Test", "top.signIn": "Anmelden" }""");
        Strings.Use("xx-YY", _folder); // a regional code uses the plain language
        Assert.Equal("xx", Strings.Current);
        Assert.Equal("Anmelden", Strings.Get("top.signIn"));
        Assert.Equal("How it works", Strings.Get("top.howItWorks"));
        Assert.Contains(("xx", "Test"), Strings.Available());
    }

    [Fact]
    public void AnUnknownLanguageFallsBackToEnglish()
    {
        Strings.Use("zz", _folder);
        Assert.Equal(Strings.Source, Strings.Current);
    }

    [Fact]
    public void ABrokenPlaceholderFallsBackToEnglish()
    {
        File.WriteAllText(Path.Combine(_folder, "xx.json"), """{ "update.available": "MyRGB {1} ist da" }""");
        Strings.Use("xx", _folder);
        Assert.Equal("MyRGB 2.0 is available", Strings.Format("update.available", "2.0"));
    }

    [GeneratedRegex(@"\{l:Tr ([\w.]+)\}|l:Tr\.Format\}, ConverterParameter=([\w.]+)")]
    private static partial Regex WindowKey();

    [GeneratedRegex(@"\{(\d+)")]
    private static partial Regex Placeholder();

    [Fact]
    public void EveryKeyTheWindowUsesHasEnglish()
    {
        Strings.Use(Strings.Source);
        var xaml = Directory.GetFiles(Path.Combine(Root, "src", "MyRGB.App"), "*.axaml", SearchOption.AllDirectories);
        var missing = xaml
            .SelectMany(f => WindowKey().Matches(File.ReadAllText(f)))
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
            .Where(k => Strings.Get(k) == k)
            .Distinct()
            .ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void TranslationsKeepEnglishPlaceholders()
    {
        Strings.Use(Strings.Source);
        var langDir = Path.Combine(Root, "src", "MyRGB.Core", "Localization", "lang");
        var english = Read(Path.Combine(langDir, "en.json"));
        var wrong = new List<string>();
        foreach (var file in Directory.GetFiles(langDir, "*.json").Where(f => Path.GetFileName(f) != "en.json"))
        foreach (var (key, text) in Read(file))
        {
            if (!english.TryGetValue(key, out var en)) { wrong.Add($"{Path.GetFileName(file)}: {key} is not in English"); continue; }
            var want = Placeholder().Matches(en).Select(m => m.Groups[1].Value).Order();
            var got = Placeholder().Matches(text).Select(m => m.Groups[1].Value).Order();
            if (!want.SequenceEqual(got)) wrong.Add($"{Path.GetFileName(file)}: {key}");
        }
        Assert.Empty(wrong);
    }

    private static Dictionary<string, string> Read(string path) =>
        System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path),
            new System.Text.Json.JsonSerializerOptions { AllowTrailingCommas = true, ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip })!;
}
