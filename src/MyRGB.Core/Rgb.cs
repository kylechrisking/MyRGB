using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyRGB.Core;

/// <summary>An 8-bit-per-channel color. Serializes to JSON as "#RRGGBB".</summary>
[JsonConverter(typeof(RgbJsonConverter))]
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb Black = new(0, 0, 0);
    public static readonly Rgb White = new(255, 255, 255);

    /// <param name="h">Hue in degrees; any value, wrapped to [0, 360).</param>
    /// <param name="s">Saturation 0..1.</param>
    /// <param name="v">Value 0..1.</param>
    public static Rgb FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);

        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - c;

        (double r, double g, double b) = (int)(h / 60) switch
        {
            0 => (c, x, 0d),
            1 => (x, c, 0d),
            2 => (0d, c, x),
            3 => (0d, x, c),
            4 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return new Rgb(ToByte(r + m), ToByte(g + m), ToByte(b + m));
    }

    public static Rgb Lerp(Rgb a, Rgb b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new Rgb(
            (byte)(a.R + (b.R - a.R) * t + 0.5),
            (byte)(a.G + (b.G - a.G) * t + 0.5),
            (byte)(a.B + (b.B - a.B) * t + 0.5));
    }

    public Rgb Scale(double factor)
    {
        factor = Math.Clamp(factor, 0, 1);
        return new Rgb((byte)(R * factor + 0.5), (byte)(G * factor + 0.5), (byte)(B * factor + 0.5));
    }

    /// <summary>Per-channel max, used to blend additive layers (e.g. ripples) without overflow.</summary>
    public static Rgb Max(Rgb a, Rgb b) => new(Math.Max(a.R, b.R), Math.Max(a.G, b.G), Math.Max(a.B, b.B));

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public override string ToString() => ToHex();

    public static Rgb Parse(string hex) =>
        TryParse(hex, out var c) ? c : throw new FormatException($"'{hex}' is not a #RRGGBB color.");

    public static bool TryParse(string? hex, out Rgb color)
    {
        color = Black;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 3) s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        if (s.Length != 6 || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return false;
        color = new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    private static byte ToByte(double v) => (byte)Math.Clamp((int)(v * 255 + 0.5), 0, 255);
}

public sealed class RgbJsonConverter : JsonConverter<Rgb>
{
    public override Rgb Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Rgb.Parse(reader.GetString() ?? "");

    public override void Write(Utf8JsonWriter writer, Rgb value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToHex());
}
