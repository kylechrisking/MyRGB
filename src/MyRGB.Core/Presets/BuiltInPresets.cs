namespace MyRGB.Core.Presets;

public static class BuiltInPresets
{
    private static Preset P(string id, string name, string effect, params (string Key, string Value)[] settings) => new()
    {
        Id = id,
        Name = name,
        EffectId = effect,
        Settings = settings.ToDictionary(s => s.Key, s => s.Value, StringComparer.OrdinalIgnoreCase),
        BuiltIn = true,
    };

    public static readonly IReadOnlyList<Preset> All =
    [
        P("rainbow-wave", "Rainbow Wave", "rainbow-wave"),
        P("spectrum", "Spectrum Cycle", "spectrum"),
        P("cyberpunk", "Cyberpunk", "gradient", ("from", "#FF00A0"), ("to", "#00F0FF"), ("speed", "3")),
        P("sunset", "Sunset", "gradient", ("from", "#FF5A00"), ("to", "#8A00C0"), ("direction", "Up"), ("speed", "1")),
        P("ocean", "Ocean", "plasma", ("palette", "Ocean"), ("speed", "2")),
        P("neon-dreams", "Neon Dreams", "plasma", ("palette", "Neon"), ("speed", "3")),
        P("aurora", "Aurora", "color-wave", ("color", "#00FF9C"), ("background", "#120038"), ("direction", "Up"), ("width", "60"), ("speed", "2")),
        P("matrix", "Matrix", "color-wave", ("color", "#00FF41"), ("background", "#000500"), ("direction", "Down"), ("count", "3"), ("width", "20"), ("speed", "5")),
        P("fire", "Fire", "fire"),
        P("starfield", "Starfield", "twinkle"),
        P("rain", "Raindrops", "ripple", ("random", "false"), ("color", "#3AA0FF"), ("rate", "1.5")),
        P("pc-heat", "PC Heat", "sensor-meter", ("sensor", "GPU temperature"), ("low", "40"), ("high", "80")),
        P("breathing-red", "Breathing Red", "breathing", ("color", "#FF0020")),
        P("arctic-white", "Arctic White", "static", ("color", "#DDEEFF")),
        P("lights-off", "Lights Off", "static", ("color", "#000000")),
    ];
}
