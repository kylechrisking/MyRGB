using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Threading;
using MyRGB.App.Services;
using MyRGB.Core.Engine;

namespace MyRGB.App.Controls;

/// <summary>
/// A strip showing the colors one device is displaying right now, left to right as its LEDs sit.
/// Drawn as smooth light, like the effect tiles: the samples go into a tiny image one pixel wide
/// per sample, and the GPU blends it as it stretches it across the bar.
/// </summary>
public sealed class DeviceColorsBar : Control
{
    public static readonly StyledProperty<AppHost?> HostProperty = AvaloniaProperty.Register<DeviceColorsBar, AppHost?>(nameof(Host));
    public static readonly StyledProperty<string?> DeviceIdProperty = AvaloniaProperty.Register<DeviceColorsBar, string?>(nameof(DeviceId));

    private const int Samples = 32;
    private static readonly IBrush Empty = new ImmutableSolidColorBrush(Color.FromRgb(22, 33, 39));

    private readonly DispatcherTimer _timer;
    private readonly int[] _pixels = new int[Samples];
    private WriteableBitmap? _bitmap;
    private AppHost? _subscribed;
    private volatile Color[]? _colors;
    private volatile string? _deviceId;
    private int[]? _order;
    private object? _orderFor;

    public DeviceColorsBar()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => InvalidateVisual());
    }

    public AppHost? Host
    {
        get => GetValue(HostProperty);
        set => SetValue(HostProperty, value);
    }

    public string? DeviceId
    {
        get => GetValue(DeviceIdProperty);
        set => SetValue(DeviceIdProperty, value);
    }

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
    }

    private void Subscribe(AppHost? host)
    {
        if (_subscribed is not null) _subscribed.Engine.FrameRendered -= OnFrame;
        _subscribed = host;
        if (host is not null) host.Engine.FrameRendered += OnFrame;
    }

    // Runs on the engine thread, so it reads the field copy, never the Avalonia property.
    private void OnFrame(FrameSnapshot frame)
    {
        var id = _deviceId;
        for (int d = 0; d < frame.Devices.Count; d++)
        {
            var device = frame.Devices[d];
            if (device.Id != id) continue;
            var colors = frame.Colors[d];
            if (colors.Length == 0) break;

            // Sort LEDs left to right once per device instance, so the strip reads like the hardware.
            if (!ReferenceEquals(_orderFor, device) || _order is null || _order.Length != colors.Length)
            {
                _order = Enumerable.Range(0, Math.Min(colors.Length, device.Leds.Count)).OrderBy(i => device.Leds[i].X).ThenBy(i => device.Leds[i].Y).ToArray();
                _orderFor = device;
            }
            int n = Math.Min(Samples, _order.Length);
            var sampled = new Color[n];
            for (int i = 0; i < n; i++)
            {
                var c = colors[_order[n == 1 ? 0 : i * (_order.Length - 1) / (n - 1)]];
                sampled[i] = Color.FromRgb(c.R, c.G, c.B);
            }
            _colors = sampled;
            return;
        }
        _colors = null; // not in the frame: ignored, or MyRGB isn't driving it
    }

    public override void Render(DrawingContext ctx)
    {
        var rect = new Rect(Bounds.Size);
        double radius = Math.Min(rect.Height / 2, 6);
        var colors = _colors;
        if (colors is null || colors.Length == 0)
        {
            ctx.DrawRectangle(Empty, null, rect, radius, radius);
            return;
        }

        // One pixel per sample in, stretched across the bar with smoothing: neighbours blend, so a
        // rainbow reads as continuous light rather than segments.
        if (_bitmap is null)
            _bitmap = new WriteableBitmap(new PixelSize(Samples, 1), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        for (int i = 0; i < Samples; i++)
        {
            var c = colors[i * colors.Length / Samples];
            _pixels[i] = unchecked((int)0xFF000000) | c.R << 16 | c.G << 8 | c.B;
        }
        using (var fb = _bitmap.Lock())
            Marshal.Copy(_pixels, 0, fb.Address, Samples);

        using var clip = ctx.PushClip(new RoundedRect(rect, radius));
        using var smooth = ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.MediumQuality });
        ctx.DrawImage(_bitmap, new Rect(0, 0, Samples, 1), rect);
    }
}
