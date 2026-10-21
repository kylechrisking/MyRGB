using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Threading;
using MyRGB.Core;
using MyRGB.Core.Effects;

namespace MyRGB.App.Controls;

/// <summary>
/// A small sample of one effect, so an effect or preset can be picked by how it looks. The effect
/// is sampled on a coarse grid sized to the control and scaled up with smoothing, so bars and
/// gradients read as soft continuous light rather than single LEDs. Runs its own copy of the effect;
/// the hardware isn't touched. With <see cref="Animate"/> off it shows a still frame a few seconds
/// in, and costs nothing.
/// </summary>
public sealed class EffectPreview : Control
{
    public static readonly StyledProperty<EffectDefinition?> DefinitionProperty =
        AvaloniaProperty.Register<EffectPreview, EffectDefinition?>(nameof(Definition));
    public static readonly StyledProperty<IReadOnlyDictionary<string, string>?> SettingsProperty =
        AvaloniaProperty.Register<EffectPreview, IReadOnlyDictionary<string, string>?>(nameof(Settings));
    public static readonly StyledProperty<bool> AnimateProperty =
        AvaloniaProperty.Register<EffectPreview, bool>(nameof(Animate), true);
    public static readonly StyledProperty<double> CornerRadiusProperty = AvaloniaProperty.Register<EffectPreview, double>(nameof(CornerRadius), 7);

    /// <summary>How far into the effect the still frame is taken, so ripples and stars have started.</summary>
    private const double StillTime = 1.1;

    /// <summary>Size of one sampled cell, in pixels.</summary>
    private const double CellSize = 7;

    private static readonly IBrush Background = new ImmutableSolidColorBrush(Color.FromRgb(4, 14, 19));

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private LedPoint[] _points = [];
    private Rgb[] _colors = [];
    private int[] _pixels = [];
    private int _cols, _rows;
    private WriteableBitmap? _bitmap;
    private MyRGB.Core.Effects.Effect? _effect;
    private double _last, _offset = StillTime;
    private bool _attached, _stale = true;

    public EffectPreview()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, (_, _) => InvalidateVisual());
        ClipToBounds = true;
    }

    public EffectDefinition? Definition { get => GetValue(DefinitionProperty); set => SetValue(DefinitionProperty, value); }
    public IReadOnlyDictionary<string, string>? Settings { get => GetValue(SettingsProperty); set => SetValue(SettingsProperty, value); }
    public bool Animate { get => GetValue(AnimateProperty); set => SetValue(AnimateProperty, value); }
    public double CornerRadius { get => GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DefinitionProperty || change.Property == SettingsProperty)
        {
            _effect = Definition?.Create(new EffectSettings(Settings));
            _stale = true;
            InvalidateVisual();
        }
        else if (change.Property == BoundsProperty) InvalidateVisual();
        else if (change.Property == AnimateProperty) UpdateTimer();
    }

    /// <summary>Sample points for the current size; rebuilt only when the grid would change.</summary>
    private void BuildGrid(Size size)
    {
        int cols = Math.Max(8, (int)Math.Round(size.Width / CellSize)), rows = Math.Max(3, (int)Math.Round(size.Height / CellSize));
        if (cols == _cols && rows == _rows) return;
        (_cols, _rows) = (cols, rows);
        _points = new LedPoint[cols * rows];
        _colors = new Rgb[cols * rows];
        _pixels = new int[cols * rows];
        _bitmap?.Dispose();
        _bitmap = new WriteableBitmap(new PixelSize(cols, rows), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                _points[r * cols + c] = LedPoint.Whole((c + 0.5f) / cols, (r + 0.5f) / rows);
        // A fresh grid means a fresh copy of the effect, so effects that keep per-LED state start clean.
        if (Definition is not null) _effect = Definition.Create(new EffectSettings(Settings));
        _stale = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        UpdateTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        UpdateTimer();
    }

    private void UpdateTimer()
    {
        bool run = _attached && Animate;
        if (run && !_timer.IsEnabled)
        {
            _clock.Restart();
            _last = 0;
            _timer.Start();
        }
        else if (!run && _timer.IsEnabled)
        {
            _offset += _clock.Elapsed.TotalSeconds; // carry on from here next time
            _timer.Stop();
            _clock.Stop();
        }
    }

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        ctx.DrawRectangle(Background, null, bounds, CornerRadius, CornerRadius);
        if (_effect is null || bounds.Width < 1 || bounds.Height < 1) return;
        BuildGrid(bounds.Size);

        if (_timer.IsEnabled)
        {
            double now = _clock.Elapsed.TotalSeconds;
            _effect!.Render(new FrameContext(_offset + now, Math.Clamp(now - _last, 0, 0.25)), _points, _colors);
            _last = now;
        }
        else if (_stale)
        {
            // Run up to the still frame in small steps, so effects that build up over time look right.
            for (double t = 0; t <= _offset; t += 1 / 15.0)
                _effect!.Render(new FrameContext(t, 1 / 15.0), _points, _colors);
        }
        _stale = false;

        // One pixel per sample; the GPU smooths it as it scales it up to the tile.
        for (int i = 0; i < _colors.Length; i++)
        {
            var color = _colors[i];
            _pixels[i] = unchecked((int)0xFF000000) | color.R << 16 | color.G << 8 | color.B;
        }
        using (var fb = _bitmap!.Lock())
            for (int r = 0; r < _rows; r++)
                Marshal.Copy(_pixels, r * _cols, fb.Address + r * fb.RowBytes, _cols);

        using var clip = ctx.PushClip(new RoundedRect(bounds, CornerRadius));
        using var smooth = ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.MediumQuality });
        ctx.DrawImage(_bitmap, new Rect(0, 0, _cols, _rows), bounds);
    }
}
