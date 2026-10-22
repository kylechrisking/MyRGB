using System.Collections.Immutable;
using System.Globalization;

namespace MyRGB.Core.Effects;

/// <summary>Describes one tweakable effect setting. The UI builds its editor from these.</summary>
public abstract record ParameterSpec(string Key, string Label)
{
    /// <summary>The default, in the same string form <see cref="EffectSettings"/> stores.</summary>
    public abstract string DefaultValue { get; }
}

public sealed record ColorParameter(string Key, string Label, Rgb Default) : ParameterSpec(Key, Label)
{
    public override string DefaultValue => Default.ToHex();
}

public sealed record NumberParameter(string Key, string Label, double Min, double Max, double Default, double Step = 1)
    : ParameterSpec(Key, Label)
{
    public override string DefaultValue => Default.ToString(CultureInfo.InvariantCulture);
}

public sealed record ChoiceParameter(string Key, string Label, IReadOnlyList<string> Options, string Default)
    : ParameterSpec(Key, Label)
{
    public override string DefaultValue => Default;
}

public sealed record BoolParameter(string Key, string Label, bool Default) : ParameterSpec(Key, Label)
{
    public override string DefaultValue => Default ? "true" : "false";
}

/// <summary>
/// An immutable bag of effect setting values, stored as invariant strings so presets round-trip
/// through JSON unchanged. Edits produce a new instance, which the engine swaps in atomically.
/// </summary>
public sealed class EffectSettings
{
    public static readonly EffectSettings Empty =
        new(ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase));

    private readonly ImmutableDictionary<string, string> _values;
    private IReadOnlyList<ParameterSpec> _specs = [];

    private EffectSettings(ImmutableDictionary<string, string> values) => _values = values;

    public EffectSettings(IReadOnlyDictionary<string, string>? values)
        : this(values?.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase)
               ?? ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase)) { }

    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>Attach specs so missing values fall back to each parameter's default.</summary>
    internal EffectSettings WithSpecs(IReadOnlyList<ParameterSpec> specs) => new(_values) { _specs = specs };

    public EffectSettings With(string key, string value) => new(_values.SetItem(key, value)) { _specs = _specs };

    public string GetString(string key) =>
        _values.TryGetValue(key, out var v) ? v : _specs.FirstOrDefault(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.DefaultValue ?? "";

    public Rgb GetColor(string key) => Rgb.TryParse(GetString(key), out var c) ? c : Rgb.White;

    /// <summary>
    /// The value, kept inside its parameter's range. Presets can come from strangers (the community),
    /// so an out-of-range, infinite or NaN number falls back rather than reaching an effect.
    /// </summary>
    public double GetNumber(string key)
    {
        var spec = _specs.OfType<NumberParameter>().FirstOrDefault(s => s.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (!double.TryParse(GetString(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d))
            return spec?.Default ?? 0;
        return spec is null ? d : Math.Clamp(d, spec.Min, spec.Max);
    }

    public bool GetBool(string key) => GetString(key).Equals("true", StringComparison.OrdinalIgnoreCase);
}
