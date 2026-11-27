using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace MyRGB.Core.Localization;

/// <summary>
/// The words MyRGB shows, in the user's language. Each language is one flat JSON file of
/// "key": "text" pairs (lang/en.json is the source). A key a translation lacks falls back to
/// English, and a key English lacks shows as itself, so a missing string is visible, never blank.
/// </summary>
/// <remarks>
/// The language is picked once at startup (<see cref="Use"/>); switching takes a restart.
/// Translators can try a file without building MyRGB by dropping it in %APPDATA%\MyRGB\lang\:
/// a file there wins over the built-in one with the same code.
/// </remarks>
public static class Strings
{
    public const string Source = "en";

    /// <summary>The key every file carries for its language's own name ("Deutsch", "Français").</summary>
    public const string NameKey = "_language";

    private static Dictionary<string, string> _english = [];
    private static Dictionary<string, string> _current = [];
    private static string? _userFolder;

    static Strings() => Use(Source);

    /// <summary>The code of the language in use ("en", "de", "pt-BR").</summary>
    public static string Current { get; private set; } = Source;

    /// <summary>
    /// Pick the language: a code from <see cref="Available"/>, or null to follow Windows' display language.
    /// Unknown codes fall back to the closest match ("de-AT" uses "de"), then English.
    /// </summary>
    public static void Use(string? code, string? userFolder = null)
    {
        _userFolder = userFolder;
        _english = Read(Source) ?? [];
        var wanted = string.IsNullOrWhiteSpace(code) ? CultureInfo.CurrentUICulture.Name : code;
        var picked = Resolve(wanted);
        Current = picked;
        _current = picked == Source ? _english : Read(picked) ?? _english;
    }

    /// <summary>The text for a key in the current language.</summary>
    public static string Get(string key) =>
        _current.TryGetValue(key, out var s) || _english.TryGetValue(key, out s) ? s : key;

    /// <summary>The text for a key with {0}, {1}… filled in, numbers written the user's way.</summary>
    public static string Format(string key, params object?[] args)
    {
        var pattern = Get(key);
        try { return string.Format(CultureInfo.CurrentCulture, pattern, args); }
        catch (FormatException)
        {
            // A translation with a broken placeholder shouldn't take the screen down; English still works.
            return _english.TryGetValue(key, out var en) ? string.Format(CultureInfo.CurrentCulture, en, args) : pattern;
        }
    }

    /// <summary>Every language MyRGB can show, as (code, name in that language), English first.</summary>
    public static IReadOnlyList<(string Code, string Name)> Available()
    {
        var codes = new SortedSet<string>(BuiltInCodes(), StringComparer.OrdinalIgnoreCase);
        foreach (var c in UserCodes()) codes.Add(c);
        codes.Remove(Source);
        var list = new List<(string, string)> { (Source, NameOf(Source)) };
        list.AddRange(codes.Select(c => (c, NameOf(c))));
        return list;
    }

    /// <summary>Keys English has that a language doesn't (yet). For translators and the tests.</summary>
    public static IReadOnlyList<string> MissingIn(string code)
    {
        var other = Read(code) ?? [];
        return _english.Keys.Where(k => k != NameKey && !other.ContainsKey(k)).ToList();
    }

    private static string NameOf(string code) =>
        Read(code) is { } d && d.TryGetValue(NameKey, out var n) ? n : code;

    private static string Resolve(string wanted)
    {
        var known = BuiltInCodes().Concat(UserCodes()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string? Match(string c) => known.FirstOrDefault(k => string.Equals(k, c, StringComparison.OrdinalIgnoreCase));
        if (Match(wanted) is { } exact) return exact;
        var dash = wanted.IndexOf('-');
        if (dash > 0 && Match(wanted[..dash]) is { } parent) return parent;
        return Source;
    }

    private static readonly Assembly Self = typeof(Strings).Assembly;
    private const string Prefix = "MyRGB.Core.Localization.lang.";

    private static IEnumerable<string> BuiltInCodes() =>
        Self.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .Select(n => n[Prefix.Length..^".json".Length]);

    private static IEnumerable<string> UserCodes()
    {
        if (_userFolder is null || !Directory.Exists(_userFolder)) return [];
        try { return Directory.GetFiles(_userFolder, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static Dictionary<string, string>? Read(string code)
    {
        // The user's own file first, so a translator sees their edits on the next start.
        if (_userFolder is not null)
        {
            var path = Path.Combine(_userFolder, code + ".json");
            try
            {
                if (File.Exists(path)) return Parse(File.ReadAllText(path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }
        using var stream = Self.GetManifestResourceStream(Prefix + code + ".json");
        if (stream is null) return null;
        try { return Parse(new StreamReader(stream).ReadToEnd()); }
        catch (JsonException) { return null; }
    }

    private static Dictionary<string, string> Parse(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json,
            new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) ?? [];
}
