using System.Collections.Concurrent;
using System.Reflection;
using MyRGB.Core.Effects;

namespace MyRGB.Scripting;

/// <summary>
/// The folder where people keep effects they wrote or downloaded (Documents\MyRGB Effects). Every .js
/// file in it becomes an effect in the list. Nothing here runs until the user picks the effect.
/// </summary>
public sealed class EffectFolder
{
    /// <summary>More than this many files and the rest are skipped, so a wrong folder can't stall startup.</summary>
    public const int MaxFiles = 200;

    private readonly EffectRegistry _registry;
    private readonly ConcurrentDictionary<string, string> _stopped = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _registered = new(StringComparer.OrdinalIgnoreCase);

    public EffectFolder(EffectRegistry registry, string? path = null)
    {
        _registry = registry;
        Path = path ?? DefaultPath;
    }

    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MyRGB Effects");

    public string Path { get; }

    /// <summary>Files that couldn't be used on the last load, and why.</summary>
    public IReadOnlyList<ScriptLoadResult> Problems { get; private set; } = [];

    /// <summary>How many effects the last load added.</summary>
    public int Loaded { get; private set; }

    /// <summary>Raised when a running script effect stops, with its id and the reason.</summary>
    public event Action<string, string>? EffectStopped;

    /// <summary>Why this effect stopped the last time it ran, or null if it hasn't.</summary>
    public string? ProblemFor(string effectId) => _stopped.GetValueOrDefault(effectId);

    /// <summary>
    /// Read every effect file and put the good ones in the effect list. Safe to call again after files
    /// change: effects whose file is gone are taken out. Does nothing if the folder doesn't exist yet.
    /// </summary>
    public IReadOnlyList<ScriptLoadResult> Load()
    {
        var results = new List<ScriptLoadResult>();
        if (Directory.Exists(Path))
        {
            foreach (var file in Directory.EnumerateFiles(Path, "*.js").Order(StringComparer.OrdinalIgnoreCase).Take(MaxFiles))
            {
                string code;
                try
                {
                    if (new FileInfo(file).Length > ScriptEffectLoader.MaxBytes)
                    {
                        results.Add(new(System.IO.Path.GetFileName(file), null, "It is too big. Effect files can be up to 256 KB."));
                        continue;
                    }
                    code = File.ReadAllText(file);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    results.Add(new(System.IO.Path.GetFileName(file), null, "MyRGB couldn't open it."));
                    continue;
                }
                results.Add(ScriptEffectLoader.Load(file, code, OnStopped));
            }
        }

        var fresh = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < results.Count; i++)
        {
            if (results[i].Definition is not { } d) continue;
            if (!fresh.Add(d.Id))
            {
                // "Ripple.js" and "ripple!.js" would both be script:ripple.
                results[i] = results[i] with { Definition = null, Problem = "Another file has nearly the same name." };
                continue;
            }
            _registry.Register(d);
            _stopped.TryRemove(d.Id, out _);
        }
        foreach (var gone in _registered.Where(id => !fresh.Contains(id)).ToList())
        {
            _registry.Remove(gone);
            _stopped.TryRemove(gone, out _);
        }
        _registered.Clear();
        _registered.UnionWith(fresh);

        Loaded = fresh.Count;
        Problems = results.Where(r => r.Definition is null).ToList();
        _registry.RaiseChanged();
        return results;
    }

    /// <summary>
    /// Make the folder if it isn't there, with a few example effects and a short guide in it, so the
    /// user has something to copy. Only called when the user asks to open the folder.
    /// </summary>
    public void Create()
    {
        if (Directory.Exists(Path) && Directory.EnumerateFileSystemEntries(Path).Any()) return;
        Directory.CreateDirectory(Path);
        var asm = typeof(EffectFolder).Assembly;
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("Examples.", StringComparison.Ordinal)))
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            using var file = File.Create(System.IO.Path.Combine(Path, name["Examples.".Length..]));
            stream.CopyTo(file);
        }
    }

    /// <summary>The examples that ship with the app, as (file name, code).</summary>
    public static IEnumerable<(string File, string Code)> Examples()
    {
        var asm = typeof(EffectFolder).Assembly;
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("Examples.", StringComparison.Ordinal) && n.EndsWith(".js", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(asm.GetManifestResourceStream(name)!);
            yield return (name["Examples.".Length..], reader.ReadToEnd());
        }
    }

    private void OnStopped(string id, string problem)
    {
        _stopped[id] = problem;
        EffectStopped?.Invoke(id, problem);
    }
}
