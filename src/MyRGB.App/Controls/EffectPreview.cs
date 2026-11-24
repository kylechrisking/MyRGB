using System.Diagnostics;
using MyRGB.App.Services;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Platform;
using Avalonia.Threading;
using MyRGB.Core;
using MyRGB.Core.Audio;
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
    private const double StillTime = 1.8;

    /// <summary>How far a still looks for its most lit frame.</summary>
    private const double StillSearch = 4.0;

    /// <summary>Size of one sampled cell, in pixels.</summary>
    private const double CellSize = 4;

    private static IBrush Background => new ImmutableSolidColorBrush(ThemeManager.Palette.Stage);

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

    /// <summary>
    /// A made-up song for thumbnails: a kick twice a second, strong bass tapering to the treble, with a
    /// little movement across the bands. Only previews see it (see <see cref="AudioFeed.Pretend"/>).
    /// </summary>
    private static AudioFrame DemoSong(double t)
    {
        double kick = Math.Exp(-(t * 2 % 1) * 5);
        var bands = new float[AudioFrame.BandCount];
        for (int b = 0; b < bands.Length; b++)
            bands[b] = (float)Math.Clamp((0.9 - b * 0.022) * (0.55 + 0.45 * kick) + 0.12 * Math.Sin(b * 1.3 + t * 3), 0, 1);
        return new AudioFrame((float)(0.5 + 0.3 * kick), bands, (long)(t * 2), bands);
    }

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        ctx.DrawRectangle(Background, null, bounds, CornerRadius, CornerRadius);
        if (_effect is null || bounds.Width < 1 || bounds.Height < 1) return;
        BuildGrid(bounds.Size);

        if (_timer.IsEnabled)
        {
            if (_stale)
            {
                // A fresh copy of the effect: play it up to where it starts showing, so effects that build
                // up over time (ripples, stars) don't open on an empty frame.
                for (double t = 0; t < _offset; t += 1 / 15.0)
                    using (AudioFeed.Pretend(DemoSong(t)))
                        _effect!.Render(new FrameContext(t, 1 / 15.0), _points, _colors);
            }
            double now = _clock.Elapsed.TotalSeconds;
            // Real music when something's playing; otherwise a made-up beat, so music effects still move.
            using (AudioFeed.Peek().Level > 0.02f ? null : AudioFeed.Pretend(DemoSong(_offset + now)))
                _effect!.Render(new FrameContext(_offset + now, Math.Clamp(now - _last, 0, 0.25)), _points, _colors);
            _last = now;
        }
        else if (_stale && _offset == StillTime)
        {
            // A still: run the first few seconds and keep the most lit frame, so ripples, stars and
            // music effects show something rather than whatever happened to be dark at one moment.
            var best = new Rgb[_colors.Length];
            long bestLight = -1;
            double bestTime = StillTime;
            for (double t = 0; t <= StillSearch; t += 1 / 15.0)
            {
                using (AudioFeed.Pretend(DemoSong(t)))
                    _effect!.Render(new FrameContext(t, 1 / 15.0), _points, _colors);
                if (t < StillTime) continue;
                long light = 0;
                foreach (var c in _colors) light += c.R + c.G + c.B;
                if (light > bestLight * 1.08) { bestLight = light; bestTime = t; Array.Copy(_colors, best, best.Length); }
            }
            Array.Copy(best, _colors, best.Length);
            _offset = bestTime; // animating carries on from the frame shown
        }
        else if (_stale)
        {
            // Run up to where the animation stopped, in small steps, so effects that build up look right.
            for (double t = 0; t <= _offset; t += 1 / 15.0)
                using (AudioFeed.Pretend(DemoSong(t)))
                    _effect!.Render(new FrameContext(t, 1 / 15.0), _points, _colors);
        }
        _stale = false;

        // One pixel per sample, softened with its neighbours (a 1-2-1 blur each way) so effects that set
        // each LED on its own, like Candle, read as glow rather than a grid; the GPU smooths the rest.
        for (int r = 0; r < _rows; r++)
        {
            for (int c = 0; c < _cols; c++)
            {
                int sr = 0, sg = 0, sb = 0, w = 0;
                for (int dr = -1; dr <= 1; dr++)
                {
                    int rr = r + dr;
                    if (rr < 0 || rr >= _rows) continue;
                    for (int dc = -1; dc <= 1; dc++)
                    {
                        int cc = c + dc;
                        if (cc < 0 || cc >= _cols) continue;
                        int k = (dr == 0 ? 2 : 1) * (dc == 0 ? 2 : 1);
                        var s = _colors[rr * _cols + cc];
                        sr += s.R * k; sg += s.G * k; sb += s.B * k; w += k;
                    }
                }
                _pixels[r * _cols + c] = unchecked((int)0xFF000000) | (sr / w) << 16 | (sg / w) << 8 | sb / w;
            }
        }
        using (var fb = _bitmap!.Lock())
            for (int r = 0; r < _rows; r++)
                Marshal.Copy(_pixels, r * _cols, fb.Address + r * fb.RowBytes, _cols);

        using var clip = ctx.PushClip(new RoundedRect(bounds, CornerRadius));
        using var smooth = ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality });
        ctx.DrawImage(_bitmap, new Rect(0, 0, _cols, _rows), bounds);
    }
}
