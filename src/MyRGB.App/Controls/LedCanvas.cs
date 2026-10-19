using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using MyRGB.App.Services;
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

    private const double Aspect = 16.0 / 9.0;
    private const double HandleSize = 10;

    private static readonly IBrush Background = new SolidColorBrush(Color.FromRgb(4, 14, 19));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)), 1);
    private static readonly IPen DevicePen = new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 1);
    private static readonly IPen ActivePen = new Pen(new SolidColorBrush(Color.FromRgb(86, 219, 143)), 1.5);
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255));
    private static readonly Typeface LabelFace = new("Inter");

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

    /// <summary>The 16:9 area the canvas occupies inside the control.</summary>
    private Rect Area()
    {
        var b = Bounds.Size;
        double w = Math.Min(b.Width, b.Height * Aspect), h = w / Aspect;
        return new Rect((b.Width - w) / 2, (b.Height - h) / 2, w, h);
    }

    private static Rect ToScreen(Rect area, DevicePlacement p) =>
        new(area.X + p.X * area.Width, area.Y + p.Y * area.Height, p.Width * area.Width, p.Height * area.Height);

    public override void Render(DrawingContext ctx)
    {
        var area = Area();
        ctx.DrawRectangle(Background, null, area, 10, 10);
        for (int i = 1; i < 8; i++)
        {
            double x = area.X + area.Width * i / 8, y = area.Y + area.Height * i / 8;
            ctx.DrawLine(GridPen, new Point(x, area.Top), new Point(x, area.Bottom));
            ctx.DrawLine(GridPen, new Point(area.Left, y), new Point(area.Right, y));
        }

        var host = Host;
        var frame = _latest;
        if (host is null) return;

        if (frame is null || frame.Devices.Count == 0)
        {
            DrawLabel(ctx, "Looking for your devices…", area.Center - new Point(80, 8), 13);
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
            bool active = device.Id == _dragId || device.Id == _hoverId;

            ctx.DrawRectangle(null, active ? ActivePen : DevicePen, rect, 4, 4);

            // LEDs: dot size from the device's density so a keyboard and a single-LED logo both read well.
            double dot = Math.Clamp(Math.Sqrt(rect.Width * rect.Height / Math.Max(1, device.Leds.Count)) * 0.55, 3, 14);
            for (int i = 0; i < device.Leds.Count && i < colors.Length; i++)
            {
                var led = device.Leds[i];
                var c = colors[i];
                var center = new Point(rect.X + 4 + led.X * (rect.Width - 8), rect.Y + 4 + led.Y * (rect.Height - 8));
                var brush = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
                ctx.DrawEllipse(brush, null, center, dot / 2, dot / 2);
            }

            // Narrow devices (a RAM stick) only show their name on hover, so neighbours' labels don't collide.
            if (rect.Width >= 46 || active)
                DrawLabel(ctx, _names.GetValueOrDefault(device.Id, device.Name), new Point(rect.X, rect.Bottom + 2), 11,
                          active ? double.PositiveInfinity : Math.Max(rect.Width + 30, 60));
            if (AllowEditing && active)
                ctx.DrawRectangle(ActivePen.Brush, null, new Rect(rect.Right - HandleSize / 2, rect.Bottom - HandleSize / 2, HandleSize, HandleSize));
        }
    }

    private static void DrawLabel(DrawingContext ctx, string text, Point at, double size, double maxWidth = double.PositiveInfinity)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFace, size, LabelBrush);
        if (!double.IsInfinity(maxWidth))
        {
            ft.MaxTextWidth = maxWidth;
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
        }
        ctx.DrawText(ft, at);
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
