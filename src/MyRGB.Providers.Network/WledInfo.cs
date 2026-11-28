using System.Text.Json;

namespace MyRGB.Providers.Network;

/// <summary>The parts of WLED's GET /json/info that MyRGB uses.</summary>
internal sealed record WledInfo(string Name, string Mac, string Version, int LedCount, int MatrixWidth, int MatrixHeight, bool Rgbw)
{
    /// <summary>Read the JSON WLED returns. Null if it isn't WLED (no "leds" object, no LED count).</summary>
    public static WledInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("leds", out var leds)) return null;
            if (!leds.TryGetProperty("count", out var countEl) || !countEl.TryGetInt32(out int count) || count <= 0) return null;

            int w = 0, h = 0;
            if (leds.TryGetProperty("matrix", out var m) && m.ValueKind == JsonValueKind.Object)
            {
                w = Int(m, "w");
                h = Int(m, "h");
                if (w * h != count) w = h = 0; // a matrix that doesn't cover every LED: lay it out as a line
            }

            // "lc" is a bit set of light capabilities: 1 = RGB, 2 = white channel. Older builds have "rgbw".
            bool rgbw = leds.TryGetProperty("lc", out var lc) && lc.TryGetInt32(out int caps) ? (caps & 2) != 0
                : leds.TryGetProperty("rgbw", out var rw) && rw.ValueKind == JsonValueKind.True;

            return new WledInfo(
                Str(root, "name") is { Length: > 0 } n ? n : "WLED",
                Str(root, "mac") ?? "",
                Str(root, "ver") ?? "",
                count, w, h, rgbw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt32(out int i) ? i : 0;
}
