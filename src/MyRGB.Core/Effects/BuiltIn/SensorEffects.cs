using MyRGB.Core.Sensors;

namespace MyRGB.Core.Effects.BuiltIn;

/// <summary>
/// Lights follow a sensor: cool colors while the PC idles, hot ones under load. "Color" turns
/// everything one color between the two; "Meter" fills the canvas like a bar gauge.
/// </summary>
internal sealed class SensorMeterEffect : Effect
{
    private static readonly string[] Sensors = SensorKeys.Followable.Select(f => f.Label).ToArray();

    public static readonly EffectDefinition Definition = new(
        "sensor-meter", "System Heat", "Your lights follow a temperature or load: cool when idle, hot when busy.", "System",
        [
            new ChoiceParameter("sensor", "Follows", Sensors, "GPU temperature"),
            new ChoiceParameter("style", "Style", ["Color", "Meter"], "Color"),
            new ColorParameter("cool", "Cool color", new Rgb(0, 200, 255)),
            new ColorParameter("hot", "Hot color", new Rgb(255, 30, 0)),
            new NumberParameter("low", "Cool at", 0, 100, 40),
            new NumberParameter("high", "Hot at", 0, 100, 80),
        ],
        () => new SensorMeterEffect());

    private double _shown = double.NaN;

    public override void Render(in FrameContext frame, ReadOnlySpan<LedPoint> leds, Span<Rgb> output)
    {
        var s = Settings;
        string label = s.GetString("sensor");
        string key = SensorKeys.Followable.FirstOrDefault(f => f.Label == label).Key ?? SensorKeys.GpuTemp;
        double low = s.GetNumber("low"), high = Math.Max(s.GetNumber("high"), low + 1);
        Rgb cool = s.GetColor("cool"), hot = s.GetColor("hot");

        // No reading (no sensor, or it isn't up yet): sit at the cool end rather than flicker.
        double target = SensorFeed.Get(key) is { } v ? Math.Clamp((v - low) / (high - low), 0, 1) : 0;
        // Sensors update once a second; ease toward each new value so the change glides.
        _shown = double.IsNaN(_shown) ? target : _shown + (target - _shown) * Math.Min(1, frame.DeltaTime * 2.5);

        if (s.GetString("style") == "Meter")
        {
            for (int i = 0; i < leds.Length; i++)
            {
                double along = leds[i].X;
                var c = Rgb.LerpHue(cool, hot, along);
                output[i] = along <= _shown ? c : c.Scale(0.06);
            }
        }
        else output.Fill(Rgb.LerpHue(cool, hot, _shown));
    }
}
