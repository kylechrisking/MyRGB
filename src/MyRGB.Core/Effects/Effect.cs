namespace MyRGB.Core.Effects;

/// <summary>One LED as an effect sees it: a point on the shared canvas, where both axes run 0..1.</summary>
public readonly record struct LedPoint(float X, float Y);

public readonly record struct FrameContext(double Time, double DeltaTime);

/// <summary>
/// Base class for all effects. An effect is a function from (canvas position, time) to color,
/// so the same effect spans every device as one continuous image, like SignalRGB's canvas.
/// </summary>
/// <remarks>
/// <see cref="Render"/> runs on the engine thread. <see cref="Settings"/> may be swapped from the
/// UI thread at any moment, so read it once into locals at the top of each render.
/// </remarks>
public abstract class Effect
{
    private volatile EffectSettings _settings = EffectSettings.Empty;

    public EffectSettings Settings
    {
        get => _settings;
        set => _settings = value;
    }

    /// <param name="leds">Every LED this effect paints, in a stable order between frames.</param>
    /// <param name="output">Same length as <paramref name="leds"/>; write one color per LED.</param>
    public abstract void Render(in FrameContext frame, ReadOnlySpan<LedPoint> leds, Span<Rgb> output);
}

/// <summary>Registry entry describing an effect: what it is called, what it exposes, how to make one.</summary>
public sealed record EffectDefinition(
    string Id,
    string Name,
    string Description,
    string Category,
    IReadOnlyList<ParameterSpec> Parameters,
    Func<Effect> Factory)
{
    public Effect Create(EffectSettings settings)
    {
        var effect = Factory();
        effect.Settings = Bind(settings);
        return effect;
    }

    /// <summary>Attach this effect's parameter defaults to a settings bag.</summary>
    public EffectSettings Bind(EffectSettings settings) => settings.WithSpecs(Parameters);
}
