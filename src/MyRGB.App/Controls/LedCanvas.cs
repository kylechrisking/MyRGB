using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
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

    private const double Aspect = 16.0 / 9.0;
    private const double HandleSize = 10;

    // Brand colors (branding/Theme.axaml): chrome for the stage, surface for device bodies.
    private static readonly IBrush Background = new ImmutableSolidColorBrush(Color.FromRgb(4, 14, 19));
    private static readonly IPen StagePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(34, 48, 57)), 1);
    private static readonly IBrush GridDot = new ImmutableSolidColorBrush(Color.FromArgb(40, 130, 146, 152));
    private static readonly IBrush BodyBrush = new ImmutableSolidColorBrush(Color.FromArgb(235, 13, 24, 30));
    private static readonly IPen DevicePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(46, 64, 75)), 1);
    private static readonly IPen ActivePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(86, 219, 143)), 1.5);
    private static readonly IBrush HandleBrush = new ImmutableSolidColorBrush(Color.FromRgb(86, 219, 143));
    private static readonly IBrush SocketBrush = new ImmutableSolidColorBrush(Color.FromRgb(28, 40, 48));
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

    /// <summary>Asks for a 16:9 box as wide as it's allowed, so the stage has no empty bands.</summary>
    protected override Size MeasureOverride(Size available)
    {
        if (double.IsInfinity(available.Width)) return new Size(0, double.IsInfinity(available.Height) ? 0 : available.Height);
        return new Size(available.Width, Math.Min(available.Width / Aspect, available.Height));
    }

    /// <summary>The 16:9 area the canvas occupies inside the control, top-left so it lines up with headings.</summary>
    private Rect Area()
    {
        var b = Bounds.Size;
        double w = Math.Min(b.Width, b.Height * Aspect), h = w / Aspect;
        return new Rect(0, 0, w, h);
    }

    private static Rect ToScreen(Rect area, DevicePlacement p) =>
        new(area.X + p.X * area.Width, area.Y + p.Y * area.Height, p.Width * area.Width, p.Height * area.Height);

    public override void Render(DrawingContext ctx)
    {
        var area = Area();
        ctx.DrawRectangle(Background, StagePen, area, 12, 12);
        for (int i = 1; i < 16; i++)
            for (int j = 1; j < 9; j++)
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
            var rect = ToScreen(area, host.Layout.GetOrCreate(device));
            bool active = device.Id == _dragId || device.Id == _hoverId || device.Id == highlight;
            int count = Math.Min(device.Leds.Count, colors.Length);
            double radius = Math.Min(8, Math.Min(rect.Width, rect.Height) / 4);

            // The device's own light spilling onto the desk around it.
            if (Average(colors, count) is { } glow)
                ctx.DrawRectangle(BodyBrush, null, new RoundedRect(rect, radius),
                    new BoxShadows(new BoxShadow { Blur = 22, Spread = 1, Color = glow }));
            ctx.DrawRectangle(BodyBrush, active ? ActivePen : DevicePen, rect, radius, radius);

            // LEDs: size from the device's density so a keyboard and a single-LED logo both read well,
            // inset so the edge LEDs sit inside the body. Each one is a soft halo under a bright core.
            double dot = Math.Clamp(Math.Sqrt(rect.Width * rect.Height / Math.Max(1, device.Leds.Count)) * 0.5, 3, 13);
            double padX = Math.Min(dot * 0.6 + 3, rect.Width / 2), padY = Math.Min(dot * 0.6 + 3, rect.Height / 2);
            for (int i = 0; i < count; i++)
            {
                var led = device.Leds[i];
                var c = colors[i];
                var center = new Point(rect.X + padX + led.X * (rect.Width - 2 * padX), rect.Y + padY + led.Y * (rect.Height - 2 * padY));
                int peak = Math.Max(c.R, Math.Max(c.G, c.B));
                if (peak < 16)
                {
                    ctx.DrawEllipse(SocketBrush, null, center, dot / 2, dot / 2);
                    continue;
                }
                ctx.DrawEllipse(new ImmutableSolidColorBrush(Color.FromArgb((byte)(peak * 0.28), c.R, c.G, c.B)), null, center, dot * 0.85, dot * 0.85);
                ctx.DrawEllipse(new ImmutableSolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), null, center, dot / 2, dot / 2);
            }

            // Narrow devices (a RAM stick) only show their name on hover, so neighbours' labels don't collide.
            if (rect.Width >= 46 || active)
                DrawLabel(ctx, area, device.Type, _names.GetValueOrDefault(device.Id, device.Name), new Point(rect.X, rect.Bottom + 6),
                          active ? double.PositiveInfinity : Math.Max(rect.Width + 24, 96), active);
            if (AllowEditing && active)
                ctx.DrawRectangle(HandleBrush, null, new Rect(rect.Right - HandleSize / 2, rect.Bottom - HandleSize / 2, HandleSize, HandleSize), 3, 3);
        }
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
