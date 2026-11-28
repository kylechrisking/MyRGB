using System.Collections.Concurrent;
using MyRGB.Core.Effects.BuiltIn;

namespace MyRGB.Core.Effects;

public sealed class EffectRegistry
{
    // Concurrent: effects from the user's folder can be reloaded while the engine thread looks them up.
    private readonly ConcurrentDictionary<string, EffectDefinition> _effects = new(StringComparer.OrdinalIgnoreCase);

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
            CometEffect.Definition,
            LavaLampEffect.Definition,
            CylonEffect.Definition,
            CandleEffect.Definition,
            SensorMeterEffect.Definition,
            MusicPulseEffect.Definition,
            VisualizerEffect.Definition,
            MusicWavesEffect.Definition,
            WallpaperEffect.Definition,
            GameEventsEffect.Definition,
            ScreenEffect.Definition,
        })
            r.Register(d);
        return r;
    }

    public IReadOnlyCollection<EffectDefinition> All => [.. _effects.Values];

    /// <summary>Add or replace an effect. Effects people write themselves come in through here.</summary>
    public void Register(EffectDefinition definition) => _effects[definition.Id] = definition;

    /// <summary>Take an effect out, e.g. when its file is deleted from the effects folder.</summary>
    public bool Remove(string id) => _effects.TryRemove(id, out _);

    /// <summary>Raised after effects were added, changed or removed at runtime, so lists can refresh.</summary>
    public event EventHandler? Changed;

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public EffectDefinition? Find(string id) => _effects.GetValueOrDefault(id);
}
