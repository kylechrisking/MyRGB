namespace MyRGB.Core.Effects;

/// <summary>
/// One LED as an effect sees it: a point on the shared canvas, where both axes run 0..1. Most effects
/// paint the canvas, so one picture flows across the whole desk. <see cref="LocalX"/> and
/// <see cref="LocalY"/> say where the LED sits within its own device (also 0..1), for effects that
/// draw the whole picture on every device, like the Visualizer's "Each device".
/// </summary>
public readonly record struct LedPoint(float X, float Y)
{
    public float LocalX { get; init; }
    public float LocalY { get; init; }

    /// <summary>A point that is its own whole device (previews, where the canvas is the device).</summary>
    public static LedPoint Whole(float x, float y) => new(x, y) { LocalX = x, LocalY = y };
}

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
