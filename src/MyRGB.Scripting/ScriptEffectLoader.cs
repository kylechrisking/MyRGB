using System.Globalization;
using System.Text.RegularExpressions;
using Acornima.Ast;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using MyRGB.Core;
using MyRGB.Core.Effects;

namespace MyRGB.Scripting;

/// <summary>A loaded effect script: the parsed code and what it says about itself.</summary>
internal sealed class ScriptSource(string id, string file, Prepared<Script> prepared, IReadOnlyList<ParameterSpec> parameters, bool usesMusic)
{
    public string Id { get; } = id;
    public string File { get; } = file;
    public Prepared<Script> Prepared { get; } = prepared;
    public IReadOnlyList<ParameterSpec> Parameters { get; } = parameters;
    public bool UsesMusic { get; } = usesMusic;

    /// <summary>Raised (on whatever thread ran it) when a running copy of this effect stops.</summary>
    public event Action<string, string>? Stopped;

    public void Report(string problem) => Stopped?.Invoke(Id, problem);
}

/// <summary>What happened when a file was read: an effect, or the reason it couldn't be used.</summary>
public sealed record ScriptLoadResult(string File, EffectDefinition? Definition, string? Problem);

/// <summary>Reads effect scripts and checks them before anything runs them on the lights.</summary>
public static partial class ScriptEffectLoader
{
    public const string IdPrefix = "script:";
    public const string Category = "Your effects";

    /// <summary>Files bigger than this are refused. Effects are small; a huge file is a mistake or a trick.</summary>
    public const int MaxBytes = 256 * 1024;

    public const int MaxSettings = 16;

    /// <summary>Read one effect from its source text. <paramref name="file"/> names it and makes its id.</summary>
    public static ScriptLoadResult Load(string file, string code, Action<string, string>? onStopped = null)
    {
        string name = Path.GetFileName(file);
        try
        {
            if (code.Length > MaxBytes) return Fail("It is too big. Effect files can be up to 256 KB.");
            var prepared = Engine.PrepareScript(code, name, strict: true);

            // Run the top level once, in its own engine, to read the name and settings.
            var probe = ScriptSandbox.CreateEngine();
            var effect = ScriptSandbox.Run(probe, prepared).AsObject();
            if (ScriptSandbox.Function(effect, "light").IsUndefined())
                return Fail("It has no light function, so it can't color anything.");
            ScriptSandbox.Function(effect, "frame"); // throws if "frame" is there but isn't a function

            string title = Text(effect, "name", 40);
            if (title.Length == 0) return Fail("It needs a name, like name: \"Northern Lights\".");
            string about = Text(effect, "description", 200);
            string author = Text(effect, "author", 40);
            if (author.Length > 0) about = (about.Length > 0 ? about.TrimEnd('.') + ". " : "") + $"Made by {author}.";
            if (about.Length == 0) about = "An effect from your effects folder.";

            var specs = ReadSettings(effect);
            bool music = MusicWord().IsMatch(code);
            var source = new ScriptSource(IdPrefix + Slug(name), name, prepared, specs, music);
            if (onStopped is not null) source.Stopped += onStopped;
            var definition = new EffectDefinition(source.Id, title, about, Category, specs, () => new ScriptEffect(source));
            return new ScriptLoadResult(name, definition, null);
        }
        catch (Exception e)
        {
            return Fail(ScriptErrors.Describe(e));
        }

        ScriptLoadResult Fail(string why) => new(name, null, why);
    }

    private static List<ParameterSpec> ReadSettings(ObjectInstance effect)
    {
        var list = new List<ParameterSpec>();
        var raw = effect.Get("settings");
        if (raw.IsUndefined() || raw.IsNull()) return list;
        if (raw is not ObjectInstance settings || raw.IsArray())
            throw new ScriptProblemException("settings should be an object, like settings: { speed: { type: \"number\", ... } }.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var keyValue in settings.GetOwnPropertyKeys())
        {
            string key = keyValue.ToString();
            if (!KeyShape().IsMatch(key))
                throw new ScriptProblemException($"The setting name \"{Clip(key)}\" should be a short word made of letters and numbers.");
            if (!seen.Add(key)) throw new ScriptProblemException($"Two settings are both called \"{key}\".");
            if (list.Count >= MaxSettings) throw new ScriptProblemException($"It has more than {MaxSettings} settings.");
            if (settings.Get(key) is not ObjectInstance s)
                throw new ScriptProblemException($"The setting \"{key}\" should be an object, like {{ type: \"number\", label: \"Speed\" }}.");

            string label = Text(s, "label", 40);
            if (label.Length == 0) label = key;
            string type = Text(s, "type", 20).ToLowerInvariant();
            var def = s.Get("default");
            list.Add(type switch
            {
                "color" => new ColorParameter(key, label, ReadColor(def)),
                "number" => ReadNumber(key, label, s),
                "choice" => ReadChoice(key, label, s),
                "switch" => new BoolParameter(key, label, def.IsBoolean() && def.AsBoolean()),
                _ => throw new ScriptProblemException($"The setting \"{key}\" needs a type: \"color\", \"number\", \"choice\" or \"switch\"."),
            });
        }
        return list;
    }

    private static Rgb ReadColor(JsValue v) => v.IsUndefined() ? Rgb.White : ScriptColor.From(v);

    private static NumberParameter ReadNumber(string key, string label, ObjectInstance s)
    {
        double min = Number(s, "min", 0), max = Number(s, "max", 100);
        if (!(max > min)) throw new ScriptProblemException($"The setting \"{key}\" needs a max bigger than its min.");
        double step = Number(s, "step", 1);
        if (!(step > 0)) step = 1;
        double def = Math.Clamp(Number(s, "default", min), min, max);
        return new NumberParameter(key, label, min, max, def, step);
    }

    private static ChoiceParameter ReadChoice(string key, string label, ObjectInstance s)
    {
        var raw = s.Get("options");
        if (!raw.IsArray()) throw new ScriptProblemException($"The setting \"{key}\" needs options, like options: [\"Soft\", \"Sharp\"].");
        var arr = raw.AsArray();
        var options = new List<string>();
        for (uint i = 0; i < arr.Length && options.Count < 12; i++)
        {
            string o = Clip(arr.Get(i).ToString(), 40).Trim();
            if (o.Length > 0 && !options.Contains(o)) options.Add(o);
        }
        if (options.Count == 0) throw new ScriptProblemException($"The setting \"{key}\" needs at least one option.");
        string def = Text(s, "default", 40);
        return new ChoiceParameter(key, label, options, options.Contains(def) ? def : options[0]);
    }

    private static double Number(ObjectInstance o, string name, double fallback)
    {
        var v = o.Get(name);
        return v.IsNumber() && double.IsFinite(v.AsNumber()) ? v.AsNumber() : fallback;
    }

    private static string Text(ObjectInstance o, string name, int max)
    {
        var v = o.Get(name);
        return v.IsString() ? Clip(v.AsString().ReplaceLineEndings(" ").Trim(), max) : "";
    }

    private static string Clip(string s, int max = 40) => s.Length > max ? s[..max] : s;

    /// <summary>"Northern Lights.js" becomes "northern-lights".</summary>
    internal static string Slug(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName).ToLower(CultureInfo.InvariantCulture);
        var slug = NotSlug().Replace(stem, "-").Trim('-');
        return slug.Length == 0 ? "effect" : slug;
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,31}$")]
    private static partial Regex KeyShape();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NotSlug();

    [GeneratedRegex(@"\bmusic\b")]
    private static partial Regex MusicWord();
}
