using MyRGB.Core.Effects.BuiltIn;

namespace MyRGB.Core.Effects;

public sealed class EffectRegistry
{
    private readonly Dictionary<string, EffectDefinition> _effects = new(StringComparer.OrdinalIgnoreCase);

    public static EffectRegistry CreateDefault()
    {
        var r = new EffectRegistry();
        foreach (var d in new[]
        {
            StaticEffect.Definition,
            BreathingEffect.Definition,
            GradientEffect.Definition,
            SpectrumCycleEffect.Definition,
            RainbowWaveEffect.Definition,
            ColorWaveEffect.Definition,
            TwinkleEffect.Definition,
            RippleEffect.Definition,
            PlasmaEffect.Definition,
            FireEffect.Definition,
        })
            r.Register(d);
        return r;
    }

    public IReadOnlyCollection<EffectDefinition> All => _effects.Values;

    /// <summary>Add or replace an effect. Third-party effect packs will come in through here.</summary>
    public void Register(EffectDefinition definition) => _effects[definition.Id] = definition;

    public EffectDefinition? Find(string id) => _effects.GetValueOrDefault(id);
}
