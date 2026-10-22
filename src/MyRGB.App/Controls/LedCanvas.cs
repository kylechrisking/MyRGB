using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Threading;
using MyRGB.App.Services;
using MyRGB.Core;
using MyRGB.Core.Devices;
using MyRGB.Core.Engine;
using MyRGB.Core.Layout;

namespace MyRGB.App.Controls;

/// <summary>
/// Live view of the effect canvas: every device drawn where it sits in the layout, with its LEDs
/// in their current colors. When <see cref="AllowEditing"/> is on, drag a device to move it and
/// drag its corner handle to resize it.
/// </summary>
public sealed class LedCanvas : Control
{
    public static readonly StyledProperty<AppHost?> HostProperty = AvaloniaProperty.Register<LedCanvas, AppHost?>(nameof(Host));
    public static readonly StyledProperty<bool> AllowEditingProperty = AvaloniaProperty.Register<LedCanvas, bool>(nameof(AllowEditing));
    public static readonly StyledProperty<string?> HighlightIdProperty = AvaloniaProperty.Register<LedCanvas, string?>(nameof(HighlightId));

    private const double Aspect = CanvasLayout.Aspect;
    private const double HandleSize = 10;

    // Brand colors (branding/Theme.axaml): chrome for the stage, surface for device bodies.
    private static readonly IBrush Background = new ImmutableSolidColorBrush(Color.FromRgb(4, 14, 19));
    private static readonly IPen StagePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(34, 48, 57)), 1);
    private static readonly IBrush GridDot = new ImmutableSolidColorBrush(Color.FromArgb(40, 130, 146, 152));
    private static readonly IBrush BodyBrush = new ImmutableSolidColorBrush(Color.FromArgb(235, 13, 24, 30));
    private static readonly IPen DevicePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(46, 64, 75)), 1);
    private static readonly IPen ActivePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(86, 219, 143)), 1.5);
    private static readonly IBrush HandleBrush = new ImmutableSolidColorBrush(Color.FromRgb(86, 219, 143));
    private static readonly IBrush LabelBrush = new ImmutableSolidColorBrush(Color.FromRgb(188, 199, 199));
    private static readonly IBrush LabelActiveBrush = new ImmutableSolidColorBrush(Color.FromRgb(226, 234, 229));
    private static readonly IBrush IconBrush = new ImmutableSolidColorBrush(Color.FromRgb(130, 146, 152));
    private static readonly Typeface LabelFace = new(new FontFamily("avares://MyRGB/Assets/Fonts#Manrope"), FontStyle.Normal, FontWeight.SemiBold);

    private readonly DispatcherTimer _timer;
    private volatile FrameSnapshot? _latest;
    private AppHost? _subscribed;
    private IReadOnlyDictionary<string, string> _names = new Dictionary<string, string>();
    private object? _namesFor;
    private bool _attached;

    private string? _dragId;
    private bool _resizing;
    private Point _dragStart;
    private DevicePlacement? _dragOrigin;
    private string? _hoverId;

    public LedCanvas()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => InvalidateVisual());
        ClipToBounds = true;
    }

    public AppHost? Host
    {
        get => GetValue(HostProperty);
        set => SetValue(HostProperty, value);
    }

    public bool AllowEditing
    {
        get => GetValue(AllowEditingProperty);
        set => SetValue(AllowEditingProperty, value);
    }

    /// <summary>A device to outline, e.g. the one the Studio is editing.</summary>
    public string? HighlightId
    {
        get => GetValue(HighlightIdProperty);
        set => SetValue(HighlightIdProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Subscribe(Host);
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        _timer.Stop();
        Subscribe(null);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HostProperty && _attached) Subscribe(Host);
    }

    private void Subscribe(AppHost? host)
    {
        if (_subscribed is not null) _subscribed.Engine.FrameRendered -= OnFrame;
        _subscribed = host;
        if (host is not null) host.Engine.FrameRendered += OnFrame;
    }

    private void OnFrame(FrameSnapshot snapshot) => _latest = snapshot;

    /// <summary>
    /// The 16:9 desk inside the control. The stage behind it fills the whole control, so any spare
    /// room shows as stage rather than gaps; the margin leaves space for labels under the bottom row.
    /// </summary>
    private Rect Area()
    {
        const double Side = 18, Top = 16, Bottom = 30;
        var b = Bounds.Size;
        double availW = Math.Max(0, b.Width - 2 * Side), availH = Math.Max(0, b.Height - Top - Bottom);
        double w = Math.Min(availW, availH * Aspect), h = w / Aspect;
        return new Rect(Side + (availW - w) / 2, Top + (availH - h) / 2, w, h);
    }

    private static Rect ToScreen(Rect area, DevicePlacement p) =>
        new(area.X + p.X * area.Width, area.Y + p.Y * area.Height, p.Width * area.Width, p.Height * area.Height);

    public override void Render(DrawingContext ctx)
    {
        var area = Area();
        ctx.DrawRectangle(Background, StagePen, new Rect(Bounds.Size).Deflate(0.5), 12, 12);
        for (int i = 0; i <= 16; i++)
            for (int j = 0; j <= 9; j++)
                ctx.DrawEllipse(GridDot, null, new Point(area.X + area.Width * i / 16, area.Y + area.Height * j / 9), 1, 1);

        var host = Host;
        var frame = _latest;
        var highlight = HighlightId;
        if (host is null) return;

        if (frame is null || frame.Devices.Count == 0)
        {
            var ft = Text("Looking for your devices…", 13, LabelBrush);
            ctx.DrawText(ft, area.Center - new Point(ft.Width / 2, ft.Height / 2));
            return;
        }

        if (!ReferenceEquals(_namesFor, frame.Devices))
        {
            _names = DeviceNames.Unique(frame.Devices);
            _namesFor = frame.Devices;
        }

        for (int d = 0; d < frame.Devices.Count; d++)
        {
            var device = frame.Devices[d];
            var colors = frame.Colors[d];
            var placement = host.Layout.GetOrCreate(device);
            var rect = ToScreen(area, placement);
            bool turned = placement.Turns(device.Leds);
            bool active = device.Id == _dragId || device.Id == _hoverId || device.Id == highlight;
            int count = Math.Min(device.Leds.Count, colors.Length);
            double radius = Math.Min(8, Math.Min(rect.Width, rect.Height) / 4);

            // The device's own light spilling onto the desk around it.
            if (Average(colors, count) is { } glow)
                ctx.DrawRectangle(BodyBrush, null, new RoundedRect(rect, radius),
                    new BoxShadows(new BoxShadow { Blur = 22, Spread = 1, Color = glow }));
            ctx.DrawRectangle(BodyBrush, active ? ActivePen : DevicePen, rect, radius, radius);

            // LEDs as smooth light, like the effect tiles: every pixel of a small image over the body
            // takes its nearest LED's color, and the image is scaled up with smoothing. Pixels far
            // from any LED stay dark, so a fan ring or a strip keeps its shape.
            double dot = DotSize(rect, device.Leds);
            double pad = Math.Min(4, Math.Min(rect.Width, rect.Height) / 4);
            var inner = rect.Deflate(pad);
            var surface = Surface(device, inner, turned, dot);
            surface.Paint(colors, count);
            using (ctx.PushClip(new RoundedRect(inner, Math.Max(2, radius - pad))))
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.MediumQuality }))
                ctx.DrawImage(surface.Bitmap, new Rect(0, 0, surface.Cols, surface.Rows), inner);

            // Narrow devices (a RAM stick) only show their name on hover, so neighbours' labels don't collide.
            if (rect.Width >= 46 || active)
                DrawLabel(ctx, area, device.Type, _names.GetValueOrDefault(device.Id, device.Name), new Point(rect.X, rect.Bottom + 6),
                          active ? double.PositiveInfinity : Math.Max(rect.Width + 24, 96), active);
            if (AllowEditing && active)
                ctx.DrawRectangle(HandleBrush, null, new Rect(rect.Right - HandleSize / 2, rect.Bottom - HandleSize / 2, HandleSize, HandleSize), 3, 3);
        }
    }

    private readonly Dictionary<string, LedSurface> _surfaces = [];

    /// <summary>The device's cached surface, rebuilt when it's moved, resized or turned.</summary>
    private LedSurface Surface(IRgbDevice device, Rect inner, bool turned, double dot)
    {
        if (_surfaces.TryGetValue(device.Id, out var s) && s.Fits(inner, turned, device.Leds.Count)) return s;
        s = new LedSurface(device, inner, turned, dot);
        _surfaces[device.Id] = s;
        return s;
    }

    /// <summary>A small image of one device's light: which LED lights each pixel, and the pixels themselves.</summary>
    private sealed class LedSurface
    {
        private static readonly int Dark = unchecked((int)0xFF0C171C);

        private readonly Size _size;
        private readonly bool _turned;
        private readonly int _ledCount;
        private readonly int[] _owner; // nearest LED per pixel, or -1 when none is close
        private readonly int[] _pixels;
        private readonly double _cell; // screen pixels per image pixel: about one LED apart, so smoothing blends neighbours

        public LedSurface(IRgbDevice device, Rect inner, bool turned, double dot)
        {
            _size = inner.Size;
            _turned = turned;
            _ledCount = device.Leds.Count;
            _cell = Math.Max(3, dot);
            Cols = Math.Max(1, (int)Math.Ceiling(inner.Width / _cell));
            Rows = Math.Max(1, (int)Math.Ceiling(inner.Height / _cell));
            _owner = new int[Cols * Rows];
            _pixels = new int[Cols * Rows];
            Bitmap = new WriteableBitmap(new PixelSize(Cols, Rows), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);

            // LED positions in image pixels, then the nearest one for every pixel within reach.
            var body = new DevicePlacement(device.Id, 0, 0, inner.Width, inner.Height);
            var points = device.Leds.Select(l => body.Map(l, turned)).Select(p => (X: p.Item1 / _cell, Y: p.Item2 / _cell)).ToArray();
            double reach = Math.Max(dot * 2, LedShapes.Of(device.Leds) is LedShape.Row or LedShape.Column ? Math.Max(inner.Width, inner.Height) : 0) / _cell;
            if (points.Length == 1) reach = double.MaxValue; // a single LED lights its whole body
            for (int r = 0; r < Rows; r++)
                for (int c = 0; c < Cols; c++)
                {
                    int best = -1;
                    double bestD = reach * reach;
                    for (int i = 0; i < points.Length; i++)
                    {
                        double dx = points[i].X - (c + 0.5), dy = points[i].Y - (r + 0.5), d = dx * dx + dy * dy;
                        if (d < bestD) { bestD = d; best = i; }
                    }
                    _owner[r * Cols + c] = best;
                }
        }

        public int Cols { get; }
        public int Rows { get; }
        public WriteableBitmap Bitmap { get; }

        public bool Fits(Rect inner, bool turned, int ledCount) =>
            Math.Abs(inner.Width - _size.Width) < 0.5 && Math.Abs(inner.Height - _size.Height) < 0.5 && turned == _turned && ledCount == _ledCount;

        public void Paint(Rgb[] colors, int count)
        {
            for (int p = 0; p < _owner.Length; p++)
            {
                int i = _owner[p];
                if (i < 0 || i >= count) { _pixels[p] = Dark; continue; }
                var c = colors[i];
                _pixels[p] = Math.Max(c.R, Math.Max(c.G, c.B)) < 16 ? Dark : unchecked((int)0xFF000000) | c.R << 16 | c.G << 8 | c.B;
            }
            using var fb = Bitmap.Lock();
            for (int r = 0; r < Rows; r++)
                Marshal.Copy(_pixels, r * Cols, fb.Address + r * fb.RowBytes, Cols);
        }
    }

    /// <summary>LED diameter: a line of LEDs is sized by its spacing and thickness, anything else by density.</summary>
    private static double DotSize(Rect rect, IReadOnlyList<LedInfo> leds)
    {
        int n = Math.Max(1, leds.Count);
        double size = LedShapes.Of(leds) is LedShape.Row or LedShape.Column
            ? Math.Min(Math.Max(rect.Width, rect.Height) / n * 0.75, Math.Min(rect.Width, rect.Height) * 0.5)
            : Math.Sqrt(rect.Width * rect.Height / n) * 0.5;
        return Math.Clamp(size, 2.5, 13);
    }

    /// <summary>The device's mean color, dimmed for use as a glow; null when it's dark.</summary>
    private static Color? Average(Rgb[] colors, int count)
    {
        if (count == 0) return null;
        long r = 0, g = 0, b = 0;
        for (int i = 0; i < count; i++) { r += colors[i].R; g += colors[i].G; b += colors[i].B; }
        byte R = (byte)(r / count), G = (byte)(g / count), B = (byte)(b / count);
        int peak = Math.Max(R, Math.Max(G, B));
        return peak < 16 ? null : Color.FromArgb((byte)(peak * 0.45), R, G, B);
    }

    private static FormattedText Text(string text, double size, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, size, brush);

    /// <summary>A device's name under it, after a small glyph for its type, kept inside the stage.</summary>
    private static void DrawLabel(DrawingContext ctx, Rect area, DeviceType type, string name, Point at, double maxWidth, bool active)
    {
        const double Icon = 12, Gap = 5;
        var ft = Text(name, 11, active ? LabelActiveBrush : LabelBrush);
        if (!double.IsInfinity(maxWidth))
        {
            ft.MaxTextWidth = Math.Max(10, maxWidth - Icon - Gap);
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
        }
        double x = Math.Max(area.X + 6, Math.Min(at.X, area.Right - 8 - Icon - Gap - ft.Width));

        using (ctx.PushTransform(Matrix.CreateScale(Icon / 24, Icon / 24) * Matrix.CreateTranslation(x, at.Y + 1)))
            ctx.DrawGeometry(active ? LabelActiveBrush : IconBrush, null, DeviceIcons.For(type));
        ctx.DrawText(ft, new Point(x + Icon + Gap, at.Y));
    }

    private (string Id, DevicePlacement Placement, bool OnHandle)? HitTest(Point p)
    {
        if (Host is not { } host || _latest is not { } frame) return null;
        var area = Area();
        // Topmost (last drawn) first.
        for (int d = frame.Devices.Count - 1; d >= 0; d--)
        {
            var placement = host.Layout.GetOrCreate(frame.Devices[d]);
            var r = ToScreen(area, placement);
            bool onHandle = new Rect(r.Right - HandleSize, r.Bottom - HandleSize, HandleSize * 2, HandleSize * 2).Contains(p);
            if (r.Inflate(2).Contains(p) || onHandle) return (frame.Devices[d].Id, placement, onHandle);
        }
        return null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!AllowEditing || HitTest(e.GetPosition(this)) is not { } hit) return;
        _dragId = hit.Id;
        _dragOrigin = hit.Placement;
        _resizing = hit.OnHandle;
        _dragStart = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pos = e.GetPosition(this);
        if (_dragId is null || _dragOrigin is null || Host is null)
        {
            var hit = AllowEditing ? HitTest(pos) : null;
            _hoverId = hit?.Id;
            Cursor = hit is null ? Cursor.Default : new Cursor(hit.Value.OnHandle ? StandardCursorType.BottomRightCorner : StandardCursorType.SizeAll);
            return;
        }

        var area = Area();
        double dx = (pos.X - _dragStart.X) / area.Width, dy = (pos.Y - _dragStart.Y) / area.Height;
        var o = _dragOrigin;
        Host.Layout.Set(_resizing
            ? o with { Width = o.Width + dx, Height = o.Height + dy }
            : o with { X = o.X + dx, Y = o.Y + dy });
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragId = null;
        _dragOrigin = null;
        e.Pointer.Capture(null);
    }
}
