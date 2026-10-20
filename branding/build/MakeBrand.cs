#:package Svg.Skia@5.2.3

// Builds every derived brand asset from the sources in branding/.
//
//   dotnet run branding/build/MakeBrand.cs [--install]
//
// Sources (edit these): logo/myrgb-mark-*.svg, icon/app-icon*.svg, streamdeck/glyph-*.svg, fonts/*.ttf.
// Outputs: the wordmark and lockup SVGs (text outlined from the bundled fonts), PNG exports of the logos,
// the app icon PNGs and .ico, and the Stream Deck images, all under branding/.
// --install also copies the icon into src/MyRGB.App/Assets and the Stream Deck images into the plugin.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;
using Svg.Skia;
using static Util;

string brand = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(ThisFile())!, ".."));
string root = Path.GetFullPath(Path.Combine(brand, ".."));
bool install = args.Contains("--install");

var ink = new Palette("#E2EAE5", "#829298", "#56DB8F", "#FF5D6E", "#56DB8F", "#42A3FD");
var inkLight = new Palette("#1D1A14", "#595549", "#007A3A", "#DE394B", "#009956", "#0D7DD4");

// --- Wordmark and lockups --------------------------------------------------
var manrope = SKTypeface.FromFile(Path.Combine(brand, "fonts", "Manrope-ExtraBold.ttf"));
var monoBold = SKTypeface.FromFile(Path.Combine(brand, "fonts", "JetBrainsMono-Bold.ttf"));
var mono = SKTypeface.FromFile(Path.Combine(brand, "fonts", "JetBrainsMono-Regular.ttf"));

foreach (var (theme, p) in new[] { ("dark", ink), ("light", inkLight) })
{
    string markBody = SvgBody(File.ReadAllText(Path.Combine(brand, "logo", $"myrgb-mark-{theme}.svg")));

    // "My" in ink, then R, G and B in their LED colors.
    var word = new Run(manrope, 96, -0.02f);
    word.Add("My", p.Ink).Add("R", p.Red).Add("G", p.Green).Add("B", p.Blue);
    Write($"logo/myrgb-wordmark-{theme}.svg", word.ToSvg(pad: 4));

    // Mark + wordmark: the mark sits 1.15x the cap height, centered on the caps.
    float cap = -manrope.ToFont(96).Metrics.CapHeight;
    float markSize = 120 * 0.95f;
    var logo = new StringBuilder();
    float wx = markSize + 22;
    logo.Append($"<g transform=\"translate(0 {F(cap / 2 - markSize / 2 - 4)}) scale(0.95)\">{markBody}</g>");
    logo.Append($"<g transform=\"translate({F(wx)} 0)\">{word.Paths()}</g>");
    var box = new SKRect(0, cap / 2 - markSize / 2 - 4, wx + word.Width, cap / 2 + markSize / 2 - 4);
    Write($"logo/myrgb-logo-{theme}.svg", Wrap(logo.ToString(), box, 8));

    // The "by root@labs:~$" lockup: the Root Labs prompt wordmark under the product name.
    var by = new Run(mono, 26, 0);
    by.Add("by ", p.Muted);
    var rl = new Run(monoBold, 26, 0, by.Width);
    rl.Add("root@labs", p.Brand).Add(":~$", p.Ink);
    float byY = 50;
    var lockup = new StringBuilder(logo.ToString());
    lockup.Append($"<g transform=\"translate({F(wx + 4)} {F(byY)})\">{by.Paths()}{rl.Paths()}</g>");
    var lbox = new SKRect(0, box.Top, Math.Max(box.Right, wx + 4 + rl.Right), Math.Max(box.Bottom, byY + 8));
    Write($"logo/myrgb-lockup-{theme}.svg", Wrap(lockup.ToString(), lbox, 8));
}

// --- Raster exports of the logos ------------------------------------------
foreach (var name in new[] { "myrgb-mark-dark", "myrgb-mark-light", "myrgb-logo-dark", "myrgb-logo-light", "myrgb-lockup-dark", "myrgb-lockup-light", "myrgb-wordmark-dark", "myrgb-wordmark-light" })
{
    using var pic = Load(File.ReadAllText(Path.Combine(brand, "logo", name + ".svg")));
    float scale = name.Contains("mark") ? 512 / pic.CullRect.Height : 2;
    SavePng(Render(pic, (int)Math.Ceiling(pic.CullRect.Width * scale), (int)Math.Ceiling(pic.CullRect.Height * scale)), $"logo/png/{name}.png");
}

// --- App icon --------------------------------------------------------------
// Each size uses the source drawn for it: tiny sizes drop the pins, which would only blur.
string IconSource(int size) => size <= 16 ? "app-icon-16.svg" : size <= 48 ? "app-icon-32.svg" : "app-icon.svg";
int[] icoSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
var icoPngs = new List<(int, byte[])>();
foreach (int size in icoSizes.Append(512))
{
    using var pic = Load(File.ReadAllText(Path.Combine(brand, "icon", IconSource(size))));
    byte[] png = Encode(Render(pic, size, size));
    Write($"icon/png/myrgb-{size}.png", png);
    if (size <= 256) icoPngs.Add((size, png));
}
Write("icon/myrgb.ico", BuildIco(icoPngs));

// --- Stream Deck -----------------------------------------------------------
// Action and category icons are white on transparent (Elgato's guideline); key images are full tiles.
string Glyph(string kind) => File.ReadAllText(Path.Combine(brand, "streamdeck", $"glyph-{kind}.svg"));
var keys = new (string Name, string Kind, Func<string, string> Color)[]
{
    ("preset-key", "preset", s => Sequential(s.Replace("stroke=\"#FFFFFF\"", $"stroke=\"{ink.Ink}\""), "fill=\"#FFFFFF\"", ink.Red, ink.Green, ink.Blue)),
    ("power-on", "power", s => s.Replace("#FFFFFF", ink.Brand)),
    ("power-off", "power", s => s.Replace("#FFFFFF", "#58666C")),
    ("brightness-key", "brightness", s => s.Replace("#FFFFFF", "#FCB442")),
    ("next-key", "next", s => s.Replace("#FFFFFF", ink.Blue)),
    ("pause-key", "pause", s => s.Replace("#FFFFFF", ink.Ink)),
    ("play-key", "play", s => s.Replace("#FFFFFF", ink.Brand)),
    ("shuffle-key", "shuffle", s => s.Replace("#FFFFFF", ink.Red)),
};
foreach (var (k, suffix) in new[] { (1, ""), (2, "@2x") })
{
    using (var plugin = Load(File.ReadAllText(Path.Combine(brand, "icon", "app-icon.svg"))))
        SavePng(Render(plugin, 256 * k, 256 * k), $"streamdeck/png/plugin{suffix}.png");
    using (var cat = Load(Glyph("category")))
        SavePng(Render(cat, 28 * k, 28 * k), $"streamdeck/png/category{suffix}.png");
    foreach (var kind in new[] { "preset", "power", "brightness", "next", "pause", "shuffle" })
    {
        using var pic = Load(Glyph(kind));
        SavePng(Render(pic, 20 * k, 20 * k), $"streamdeck/png/actions/{kind}{suffix}.png");
    }
    foreach (var key in keys)
    {
        int s = 72 * k;
        using var surface = SKSurface.Create(new SKImageInfo(s, s, SKColorType.Rgba8888, SKAlphaType.Premul));
        var c = surface.Canvas;
        c.Clear(SKColor.Parse("#0D181E"));
        using var pic = Load(key.Color(Glyph(key.Kind)));
        // Upper part of the key, leaving room for the title Stream Deck draws at the bottom.
        DrawFit(c, pic, new SKRect(s * 0.26f, s * 0.16f, s * 0.74f, s * 0.64f));
        SavePng(surface.Snapshot(), $"streamdeck/png/keys/{key.Name}{suffix}.png");
    }
}

if (install)
{
    Copy("icon/myrgb.ico", "src/MyRGB.App/Assets/icon.ico");
    Copy("icon/png/myrgb-256.png", "src/MyRGB.App/Assets/icon.png");
    Copy("logo/png/myrgb-mark-dark.png", "src/MyRGB.App/Assets/mark.png");
    string sd = Path.Combine(brand, "streamdeck", "png");
    foreach (var file in Directory.GetFiles(sd, "*.png", SearchOption.AllDirectories))
        Copy(Path.Combine("streamdeck", "png", Path.GetRelativePath(sd, file)),
             Path.Combine("streamdeck", "com.myrgb.streamdeck.sdPlugin", "imgs", Path.GetRelativePath(sd, file)));
    foreach (var font in Directory.GetFiles(Path.Combine(brand, "fonts")))
        Copy(Path.Combine("fonts", Path.GetFileName(font)), Path.Combine("src", "MyRGB.App", "Assets", "Fonts", Path.GetFileName(font)));
}
Console.WriteLine($"Brand assets written under branding/{(install ? ", and installed into the app and plugin" : "")}.");

// --- Helpers ---------------------------------------------------------------

void Copy(string fromBrand, string toRoot)
{
    string dest = Path.Combine(root, toRoot);
    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
    File.Copy(Path.Combine(brand, fromBrand), dest, overwrite: true);
}

void Write(string rel, object content)
{
    string path = Path.Combine(brand, rel);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    if (content is byte[] b) File.WriteAllBytes(path, b);
    else File.WriteAllText(path, (string)content, new UTF8Encoding(false));
}

static SKPicture Load(string svgText)
{
    var svg = new SKSvg();
    svg.FromSvg(svgText);
    return svg.Picture ?? throw new InvalidOperationException("SVG did not render");
}

static void DrawFit(SKCanvas c, SKPicture pic, SKRect dest)
{
    var src = pic.CullRect;
    float scale = Math.Min(dest.Width / src.Width, dest.Height / src.Height);
    c.Save();
    c.Translate(dest.MidX - src.MidX * scale, dest.MidY - src.MidY * scale);
    c.Scale(scale);
    c.DrawPicture(pic);
    c.Restore();
}

static SKImage Render(SKPicture pic, int w, int h)
{
    using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
    surface.Canvas.Clear(SKColors.Transparent);
    DrawFit(surface.Canvas, pic, new SKRect(0, 0, w, h));
    return surface.Snapshot();
}

static byte[] Encode(SKImage img)
{
    using (img) using (var data = img.Encode(SKEncodedImageFormat.Png, 100)) return data.ToArray();
}

void SavePng(SKImage img, string rel) => Write(rel, Encode(img));

// Multi-size .ico with PNG entries (supported since Windows Vista).
static byte[] BuildIco(List<(int Size, byte[] Png)> images)
{
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)images.Count);
    int offset = 6 + 16 * images.Count;
    foreach (var (size, png) in images)
    {
        byte dim = size >= 256 ? (byte)0 : (byte)size;
        w.Write(dim); w.Write(dim); w.Write((byte)0); w.Write((byte)0);
        w.Write((ushort)1); w.Write((ushort)32); w.Write((uint)png.Length); w.Write((uint)offset);
        offset += png.Length;
    }
    foreach (var (_, png) in images) w.Write(png);
    w.Flush();
    return ms.ToArray();
}

static string SvgBody(string svg)
{
    var m = Regex.Match(svg, @"<svg[^>]*>(.*)</svg>", RegexOptions.Singleline);
    return Regex.Replace(m.Groups[1].Value, @"<!--.*?-->", "", RegexOptions.Singleline).Trim();
}

// Replaces each occurrence of `find` in turn with the attribute recolored to the next color.
static string Sequential(string s, string find, params string[] colors)
{
    int i = 0;
    return Regex.Replace(s, Regex.Escape(find), _ => i < colors.Length ? find.Replace("#FFFFFF", colors[i++]) : find);
}

static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

record Palette(string Ink, string Muted, string Brand, string Red, string Green, string Blue);

/// <summary>A line of text laid out glyph by glyph and outlined to SVG paths, one color per span.</summary>
sealed class Run(SKTypeface face, float size, float trackingEm, float startX = 0)
{
    private readonly SKFont _font = face.ToFont(size);
    private readonly StringBuilder _paths = new();
    private float _x = startX;
    private SKRect _bounds = SKRect.Empty;

    public float Width => _x;
    public float Right => _x - size * trackingEm;

    public Run Add(string text, string color)
    {
        var d = new StringBuilder();
        foreach (var ch in text)
        {
            using var path = _font.GetTextPath(ch.ToString(), new SKPoint(_x, 0));
            if (!path.IsEmpty)
            {
                d.Append(path.ToSvgPathData());
                _bounds = _bounds.IsEmpty ? path.Bounds : SKRect.Union(_bounds, path.Bounds);
            }
            _x += _font.MeasureText(ch.ToString()) + size * trackingEm;
        }
        _paths.Append($"<path fill=\"{color}\" d=\"{d}\"/>");
        return this;
    }

    public string Paths() => _paths.ToString();

    public string ToSvg(float pad) => Util.Wrap(Paths(), _bounds, pad);
}

static class Util
{
    public static string Wrap(string body, SKRect box, float pad) =>
        $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{F(box.Left - pad)} {F(box.Top - pad)} {F(box.Width + 2 * pad)} {F(box.Height + 2 * pad)}\" " +
        $"width=\"{F(box.Width + 2 * pad)}\" height=\"{F(box.Height + 2 * pad)}\">\n" +
        "<!-- Built from the bundled fonts by branding/build/MakeBrand.cs. Don't edit by hand. -->\n" + body + "\n</svg>\n";

    public static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
