using System.Text.Json;

namespace MyRGB.Core.Presets;

/// <summary>Reads and writes a single preset as a plain JSON file, so presets can be shared by file
/// instead of needing an account.</summary>
public static class PresetFile
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void Export(Preset preset, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(preset with { BuiltIn = false }, Json));

    public static Preset Import(string path)
    {
        Preset? preset;
        try { preset = JsonSerializer.Deserialize<Preset>(File.ReadAllText(path), Json); }
        catch (JsonException) { throw new InvalidDataException("That file isn't a MyRGB preset."); }
        if (preset is null || string.IsNullOrWhiteSpace(preset.Name) || string.IsNullOrWhiteSpace(preset.EffectId))
            throw new InvalidDataException("That file isn't a MyRGB preset.");
        return preset;
    }

    /// <summary>A filesystem-safe file name for a preset, so exporting "Ocean ~ Deep" doesn't fail.</summary>
    public static string FileName(string presetName)
    {
        var slug = new string(presetName.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return $"{(slug.Length > 0 ? slug : "preset")}.myrgb-preset.json";
    }
}
