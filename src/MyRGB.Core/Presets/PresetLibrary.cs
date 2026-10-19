using MyRGB.Core.Effects;

namespace MyRGB.Core.Presets;

/// <summary>Built-in presets plus the user's own. User presets are persisted by the app via <see cref="Changed"/>.</summary>
public sealed class PresetLibrary
{
    private readonly object _gate = new();
    private readonly List<Preset> _user;

    public PresetLibrary(IEnumerable<Preset>? userPresets = null) => _user = userPresets?.ToList() ?? [];

    public event EventHandler? Changed;

    public IReadOnlyList<Preset> BuiltIns { get; } = BuiltInPresets.All;

    public IReadOnlyList<Preset> UserPresets
    {
        get { lock (_gate) return _user.ToList(); }
    }

    /// <summary>User presets first (most likely to be wanted), then built-ins.</summary>
    public IReadOnlyList<Preset> All
    {
        get { lock (_gate) return [.. _user, .. BuiltIns]; }
    }

    public Preset? Find(string id) => All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Add or replace a user preset. Saving over a built-in's id stores a user copy that shadows it.</summary>
    public Preset Save(Preset preset)
    {
        var stored = preset with { BuiltIn = false };
        lock (_gate)
        {
            int i = _user.FindIndex(p => p.Id.Equals(stored.Id, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) _user[i] = stored;
            else _user.Insert(0, stored);
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return stored;
    }

    public bool Delete(string id)
    {
        bool removed;
        lock (_gate) removed = _user.RemoveAll(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed) Changed?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    public static string NewId(string name)
    {
        var slug = new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return $"user-{(slug.Length > 0 ? slug : "preset")}-{Guid.NewGuid().ToString("N")[..6]}";
    }

    /// <summary>
    /// A few representative colors of a preset, used for preset cards in the UI and Stream Deck key
    /// images. Samples a diagonal across the canvas (so vertical waves show up too) over a few
    /// seconds of animation and keeps the most telling frame: bright, with the widest spread of
    /// colors. So a breathing effect isn't caught mid-exhale and a wave shows its bands.
    /// </summary>
    public static Rgb[] Swatch(Preset preset, EffectRegistry effects, int count = 5)
    {
        var def = effects.Find(preset.EffectId);
        if (def is null) return [Rgb.Black];
        var effect = def.Create(new EffectSettings(preset.Settings));
        var points = Enumerable.Range(0, count)
            .Select(i => count == 1 ? new LedPoint(0.5f, 0.5f) : new LedPoint(i / (float)(count - 1), i / (float)(count - 1)))
            .ToArray();
        var frame = new Rgb[count];
        var best = new Rgb[count];
        double bestScore = -1;
        const double fps = 15;
        for (int f = 0; f < 6 * fps; f++)
        {
            effect.Render(new FrameContext(f / fps, 1 / fps), points, frame);
            if (f < fps) continue; // let stateful effects (ripples, stars) get going first
            double score = Score(frame);
            if (score > bestScore)
            {
                bestScore = score;
                frame.CopyTo(best, 0);
            }
        }
        return best;
    }

    /// <summary>Mean brightness plus how far the colors spread from their average (both 0..~1).</summary>
    private static double Score(Rgb[] colors)
    {
        double mr = colors.Average(c => c.R), mg = colors.Average(c => c.G), mb = colors.Average(c => c.B);
        double brightness = (mr + mg + mb) / (3 * 255);
        double spread = colors.Average(c => Math.Sqrt((c.R - mr) * (c.R - mr) + (c.G - mg) * (c.G - mg) + (c.B - mb) * (c.B - mb))) / 255;
        return brightness + 1.5 * spread;
    }
}
