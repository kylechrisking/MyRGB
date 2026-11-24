using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using MyRGB.App.Services;
using MyRGB.Core.Devices;
using MyRGB.Core.Engine;

namespace MyRGB.App.Controls;

/// <summary>
/// A small drawing of a device, lit the way it is right now. The outlines are MyRGB's own generic
/// shapes per kind of device (a keyboard, a mouse, a RAM stick), never a product's likeness; the
/// lights are the device's real LEDs at their real positions, in the colors the engine just sent.
/// </summary>
public sealed class DeviceThumb : Control
{
    public static readonly StyledProperty<AppHost?> HostProperty = AvaloniaProperty.Register<DeviceThumb, AppHost?>(nameof(Host));
    public static readonly StyledProperty<string?> DeviceIdProperty = AvaloniaProperty.Register<DeviceThumb, string?>(nameof(DeviceId));
    public static readonly StyledProperty<DeviceType> KindProperty = AvaloniaProperty.Register<DeviceThumb, DeviceType>(nameof(Kind));

    private readonly DispatcherTimer _timer;
    private AppHost? _subscribed;
    private volatile string? _deviceId;
    private volatile Lit? _lit;

    /// <summary>One frame's worth for this device: LED positions (unit square) and their colors.</summary>
    private sealed record Lit(Point[] Positions, Color[] Colors);

    private Point[]? _positions;
    private object? _positionsFor;

    public DeviceThumb()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Render, (_, _) => InvalidateVisual());
    }

    public AppHost? Host { get => GetValue(HostProperty); set => SetValue(HostProperty, value); }
    public string? DeviceId { get => GetValue(DeviceIdProperty); set => SetValue(DeviceIdProperty, value); }
    public DeviceType Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe(Host);
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
        Subscribe(null);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HostProperty && _timer.IsEnabled) Subscribe(Host);
        if (change.Property == DeviceIdProperty) _deviceId = DeviceId;
        if (change.Property == KindProperty) InvalidateVisual();
    }

    private void Subscribe(AppHost? host)
    {
        if (_subscribed is not null) _subscribed.Engine.FrameRendered -= OnFrame;
        _subscribed = host;
        if (host is not null) host.Engine.FrameRendered += OnFrame;
    }

    // Engine thread: copy what Render needs, never touch Avalonia properties here.
    private void OnFrame(FrameSnapshot frame)
    {
        var id = _deviceId;
        for (int d = 0; d < frame.Devices.Count; d++)
        {
            var device = frame.Devices[d];
            if (device.Id != id) continue;
            var colors = frame.Colors[d];
            int n = Math.Min(colors.Length, device.Leds.Count);
            if (!ReferenceEquals(_positionsFor, device) || _positions is null || _positions.Length != n)
            {
                _positions = Enumerable.Range(0, n).Select(i => new Point(device.Leds[i].X, device.Leds[i].Y)).ToArray();
                _positionsFor = device;
            }
            var copy = new Color[n];
            for (int i = 0; i < n; i++) copy[i] = Color.FromRgb(colors[i].R, colors[i].G, colors[i].B);
            _lit = new Lit(_positions, copy);
            return;
        }
        _lit = null; // left alone, or not driven by MyRGB
    }

    private IBrush Brush(string key, Color fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : new ImmutableSolidColorBrush(fallback);

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size).Deflate(4);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var body = Brush("ScreenWellBrush", Color.Parse("#1E1C19"));
        var line = new Pen(Brush("InkSecondaryBrush", Color.Parse("#45413A")), 1.4);
        var detail = new Pen(Brush("HairlineBrush", Color.Parse("#3A3632")), 1);

        // Each kind of device: its aspect ratio, how to draw it, and the area its LEDs sit in.
        double aspect = Kind switch
        {
            DeviceType.Keyboard or DeviceType.Keypad => 3.0,
            DeviceType.Mouse => 0.62,
            DeviceType.Headset => 1.25,
            DeviceType.HeadsetStand => 0.8,
            DeviceType.Memory => 4.2,
            DeviceType.Gpu => 3.2,
            DeviceType.LedStrip or DeviceType.Cooler or DeviceType.Case => 5.0,
            DeviceType.Motherboard => 0.9,
            DeviceType.MouseMat => 1.8,
            _ => 1.6,
        };
        var r = Fit(bounds, aspect);
        Rect ledArea;
        bool keys = false;

        switch (Kind)
        {
            case DeviceType.Keyboard:
            case DeviceType.Keypad:
                ctx.DrawRectangle(body, line, r, 6, 6);
                ledArea = r.Deflate(new Thickness(r.Width * 0.04, r.Height * 0.1));
                keys = true;
                break;

            case DeviceType.Mouse:
            {
                ctx.DrawRectangle(body, line, r, r.Width * 0.48, r.Width * 0.48);
                double split = r.Y + r.Height * 0.38;
                ctx.DrawLine(detail, new Point(r.Center.X, r.Y + 2), new Point(r.Center.X, split));
                ctx.DrawLine(detail, new Point(r.X + 3, split), new Point(r.Right - 3, split));
                var wheel = new Rect(r.Center.X - r.Width * 0.06, r.Y + r.Height * 0.12, r.Width * 0.12, r.Height * 0.16);
                ctx.DrawRectangle(null, line, wheel, 3, 3);
                ledArea = r.Deflate(new Thickness(r.Width * 0.18, r.Height * 0.2));
                break;
            }

            case DeviceType.Headset:
            {
                var cupW = r.Width * 0.24;
                var cupH = r.Height * 0.48;
                var left = new Rect(r.X, r.Bottom - cupH, cupW, cupH);
                var right = new Rect(r.Right - cupW, r.Bottom - cupH, cupW, cupH);
                var band = new StreamGeometry();
                using (var g = band.Open())
                {
                    g.BeginFigure(new Point(left.Center.X, left.Y + 2), false);
                    g.ArcTo(new Point(right.Center.X, right.Y + 2), new Size(r.Width * 0.38, r.Height * 0.62), 0, false, SweepDirection.Clockwise);
                }
                ctx.DrawGeometry(null, new Pen(line.Brush, 3.2), band);
                ctx.DrawRectangle(body, line, left, cupW * 0.4, cupW * 0.4);
                ctx.DrawRectangle(body, line, right, cupW * 0.4, cupW * 0.4);
                ledArea = new Rect(left.X + cupW * 0.25, left.Y + cupH * 0.3, r.Width - cupW * 0.5, cupH * 0.4);
                break;
            }

            case DeviceType.HeadsetStand:
            {
                var foot = new Rect(r.X, r.Bottom - r.Height * 0.22, r.Width, r.Height * 0.22);
                var post = new Rect(r.Center.X - r.Width * 0.06, r.Y + r.Height * 0.12, r.Width * 0.12, r.Height * 0.68);
                ctx.DrawRectangle(body, line, post, 2, 2);
                ctx.DrawRectangle(body, line, new Rect(r.Center.X - r.Width * 0.3, r.Y + r.Height * 0.06, r.Width * 0.6, r.Height * 0.09), 4, 4);
                ctx.DrawRectangle(body, line, foot, foot.Height / 2, foot.Height / 2);
                ledArea = foot.Deflate(new Thickness(foot.Width * 0.1, foot.Height * 0.3));
                break;
            }

            case DeviceType.Memory:
            {
                ctx.DrawRectangle(body, line, r, 3, 3);
                double chipW = r.Width / 6;
                for (int i = 0; i < 4; i++)
                    ctx.DrawRectangle(null, detail, new Rect(r.X + chipW * (0.6 + i * 1.25), r.Y + r.Height * 0.5, chipW * 0.9, r.Height * 0.32), 1, 1);
                ctx.DrawLine(detail, new Point(r.X + r.Width * 0.55, r.Bottom), new Point(r.X + r.Width * 0.55, r.Bottom - 4));
                ledArea = new Rect(r.X + 4, r.Y + 3, r.Width - 8, r.Height * 0.28);
                break;
            }

            case DeviceType.Gpu:
            {
                ctx.DrawRectangle(body, line, r, 5, 5);
                double fan = r.Height * 0.62;
                for (int i = 0; i < 2; i++)
                    ctx.DrawEllipse(null, detail, new Point(r.X + r.Width * (0.3 + i * 0.4), r.Center.Y + r.Height * 0.08), fan / 2, fan / 2);
                ledArea = new Rect(r.X + 6, r.Y + 3, r.Width - 12, r.Height * 0.16);
                break;
            }

            case DeviceType.Motherboard:
            {
                ctx.DrawRectangle(body, line, r, 3, 3);
                ctx.DrawRectangle(null, detail, new Rect(r.X + r.Width * 0.35, r.Y + r.Height * 0.12, r.Width * 0.3, r.Height * 0.28), 2, 2);
                for (int i = 0; i < 3; i++)
                    ctx.DrawLine(detail, new Point(r.X + r.Width * 0.12, r.Y + r.Height * (0.55 + i * 0.13)), new Point(r.X + r.Width * 0.88, r.Y + r.Height * (0.55 + i * 0.13)));
                ledArea = r.Deflate(new Thickness(r.Width * 0.08));
                break;
            }

            case DeviceType.LedStrip:
            case DeviceType.Cooler:
            case DeviceType.Case:
                ctx.DrawRectangle(body, line, r, r.Height / 2, r.Height / 2);
                ledArea = r.Deflate(new Thickness(r.Height * 0.5, r.Height * 0.25));
                break;

            default:
                ctx.DrawRectangle(body, line, r, 6, 6);
                ledArea = r.Deflate(new Thickness(r.Width * 0.1, r.Height * 0.15));
                break;
        }

        if (_lit is not { } lit || lit.Colors.Length == 0) return;
        DrawLeds(ctx, lit, ledArea, keys);
    }

    /// <summary>The largest rectangle of the given aspect (width / height) centered in <paramref name="box"/>.</summary>
    private static Rect Fit(Rect box, double aspect)
    {
        double w = box.Width, h = w / aspect;
        if (h > box.Height) { h = box.Height; w = h * aspect; }
        return new Rect(box.Center.X - w / 2, box.Center.Y - h / 2, w, h);
    }

    private static void DrawLeds(DrawingContext ctx, Lit lit, Rect area, bool keys)
    {
        int n = lit.Colors.Length;
        // Spread the LEDs over the area; a device whose LEDs all share one row or column stays a line.
        double minX = lit.Positions.Min(p => p.X), maxX = lit.Positions.Max(p => p.X);
        double minY = lit.Positions.Min(p => p.Y), maxY = lit.Positions.Max(p => p.Y);
        double spanX = maxX - minX, spanY = maxY - minY;

        // Dot size from how densely they're packed, so a 102-key board and a 6-LED board both read well.
        double cell = Math.Sqrt(area.Width * area.Height / Math.Max(1, n));
        double size = Math.Clamp(cell * (keys ? 0.78 : 0.6), 2.2, Math.Min(area.Height, 14));

        for (int i = 0; i < n; i++)
        {
            var p = lit.Positions[i];
            double x, y;
            if (spanX < 1e-6 && spanY < 1e-6)
            {
                // No layout to go on (every LED at one spot): spread them along the light, in order.
                x = n == 1 ? area.Center.X : area.X + i * area.Width / (n - 1);
                y = area.Center.Y;
            }
            else
            {
                x = spanX < 1e-6 ? area.Center.X : area.X + (p.X - minX) / spanX * area.Width;
                y = spanY < 1e-6 ? area.Center.Y : area.Y + (p.Y - minY) / spanY * area.Height;
            }
            var c = lit.Colors[i];
            if (c.R + c.G + c.B < 12) continue; // dark LED: let the body show

            // A soft halo first, then the LED itself, so the device glows rather than shows dots.
            ctx.DrawEllipse(new ImmutableSolidColorBrush(Color.FromArgb(70, c.R, c.G, c.B)), null, new Point(x, y), size * 0.95, size * 0.95);
            var core = new ImmutableSolidColorBrush(c);
            if (keys) ctx.DrawRectangle(core, null, new Rect(x - size / 2, y - size / 2, size, size), 1.5, 1.5);
            else ctx.DrawEllipse(core, null, new Point(x, y), size / 2, size / 2);
        }
    }
}
