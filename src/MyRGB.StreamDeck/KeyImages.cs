using System.Net;
using System.Text;

namespace MyRGB.StreamDeck;

/// <summary>Key images drawn as SVG data URIs, so preset keys can show the preset's real colors.</summary>
internal static class KeyImages
{
    /// <summary>The preset's swatch as a soft gradient, with a highlight ring when it's the active preset.</summary>
    public static string Preset(IReadOnlyList<string> swatch, bool active, bool lightsOn)
    {
        var stops = new StringBuilder();
        for (int i = 0; i < swatch.Count; i++)
            stops.Append($"<stop offset=\"{(swatch.Count == 1 ? 0 : i * 100 / (swatch.Count - 1))}%\" stop-color=\"{WebUtility.HtmlEncode(swatch[i])}\"/>");

        string ring = active
            ? "<rect x=\"6\" y=\"6\" width=\"132\" height=\"132\" rx=\"22\" fill=\"none\" stroke=\"#FFFFFF\" stroke-width=\"6\"/>"
            : "";
        string dim = lightsOn ? "" : "<rect width=\"144\" height=\"144\" fill=\"#000\" opacity=\"0.6\"/>";

        return Svg(
            "<defs><linearGradient id=\"g\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\">" + stops + "</linearGradient>" +
            "<linearGradient id=\"shade\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\"><stop offset=\"45%\" stop-color=\"#000\" stop-opacity=\"0\"/><stop offset=\"100%\" stop-color=\"#000\" stop-opacity=\"0.75\"/></linearGradient></defs>" +
            "<rect width=\"144\" height=\"144\" fill=\"#101018\"/>" +
            "<rect x=\"12\" y=\"12\" width=\"120\" height=\"120\" rx=\"18\" fill=\"url(#g)\"/>" +
            "<rect x=\"12\" y=\"12\" width=\"120\" height=\"120\" rx=\"18\" fill=\"url(#shade)\"/>" +
            ring + dim);
    }

    /// <summary>A brightness meter: a sun glyph over a fill bar.</summary>
    public static string Brightness(double level, bool lightsOn)
    {
        int width = (int)Math.Round(Math.Clamp(level, 0, 1) * 104);
        string color = lightsOn ? "#FFD250" : "#6E6E78";
        return Svg(
            "<rect width=\"144\" height=\"144\" fill=\"#101018\"/>" +
            $"<circle cx=\"72\" cy=\"54\" r=\"14\" fill=\"{color}\"/>" +
            $"<g stroke=\"{color}\" stroke-width=\"6\" stroke-linecap=\"round\">" +
            string.Concat(Enumerable.Range(0, 8).Select(i =>
            {
                double a = i * Math.PI / 4;
                return $"<line x1=\"{72 + Math.Cos(a) * 24:0}\" y1=\"{54 + Math.Sin(a) * 24:0}\" x2=\"{72 + Math.Cos(a) * 32:0}\" y2=\"{54 + Math.Sin(a) * 32:0}\"/>";
            })) + "</g>" +
            "<rect x=\"20\" y=\"98\" width=\"104\" height=\"12\" rx=\"6\" fill=\"#2A2A38\"/>" +
            $"<rect x=\"20\" y=\"98\" width=\"{width}\" height=\"12\" rx=\"6\" fill=\"{color}\"/>");
    }

    private static string Svg(string body) =>
        "data:image/svg+xml;charset=utf8," + Uri.EscapeDataString(
            $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"144\" height=\"144\" viewBox=\"0 0 144 144\">{body}</svg>");
}
