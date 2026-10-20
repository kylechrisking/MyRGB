using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using MyRGB.Core;
using MyRGB.Core.Effects;

namespace MyRGB.App.Controls;

/// <summary>
/// A small sample of one effect, drawn as a grid of LEDs, so an effect or preset can be picked by
/// how it looks. Runs its own copy of the effect; the hardware isn't touched. With
/// <see cref="Animate"/> off it shows a still frame a few seconds in, and costs nothing.
/// </summary>
public sealed class EffectPreview : Control
{
    public static readonly StyledProperty<EffectDefinition?> DefinitionProperty =
        AvaloniaProperty.Register<EffectPreview, EffectDefinition?>(nameof(Definition));
    public static readonly StyledProperty<IReadOnlyDictionary<string, string>?> SettingsProperty =
        AvaloniaProperty.Register<EffectPreview, IReadOnlyDictionary<string, string>?>(nameof(Settings));
    public static readonly StyledProperty<bool> AnimateProperty =
        AvaloniaProperty.Register<EffectPreview, bool>(nameof(Animate), true);
    public static readonly StyledProperty<int> ColumnsProperty = AvaloniaProperty.Register<EffectPreview, int>(nameof(Columns), 22);
    public static readonly StyledProperty<int> RowsProperty = AvaloniaProperty.Register<EffectPreview, int>(nameof(Rows), 8);
    public static readonly StyledProperty<double> CornerRadiusProperty = AvaloniaProperty.Register<EffectPreview, double>(nameof(CornerRadius), 7);

    /// <summary>How far into the effect the still frame is taken, so ripples and stars have started.</summary>
    private const double StillTime = 1.1;
    private static readonly IBrush Background = new ImmutableSolidColorBrush(Color.FromRgb(4, 14, 19));

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private LedPoint[] _points = [];
    private Rgb[] _colors = [];
    private MyRGB.Core.Effects.Effect? _effect;
    private double _last, _offset = StillTime;
    private bool _attached, _stale = true;

    public EffectPreview()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, (_, _) => InvalidateVisual());
        ClipToBounds = true;
        BuildGrid();
    }

    public EffectDefinition? Definition { get => GetValue(DefinitionProperty); set => SetValue(DefinitionProperty, value); }
    public IReadOnlyDictionary<string, string>? Settings { get => GetValue(SettingsProperty); set => SetValue(SettingsProperty, value); }
    public bool Animate { get => GetValue(AnimateProperty); set => SetValue(AnimateProperty, value); }
    public int Columns { get => GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
    public int Rows { get => GetValue(RowsProperty); set => SetValue(RowsProperty, value); }
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
        else if (change.Property == ColumnsProperty || change.Property == RowsProperty)
        {
            BuildGrid();
            _stale = true;
            InvalidateVisual();
        }
        else if (change.Property == AnimateProperty) UpdateTimer();
    }

    private void BuildGrid()
    {
        int cols = Math.Max(2, Columns), rows = Math.Max(1, Rows);
        _points = new LedPoint[cols * rows];
        _colors = new Rgb[cols * rows];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                _points[r * cols + c] = new LedPoint(c / (float)(cols - 1), rows == 1 ? 0.5f : r / (float)(rows - 1));
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
        if (_effect is null) return;

        if (_timer.IsEnabled)
        {
            double now = _clock.Elapsed.TotalSeconds;
            _effect.Render(new FrameContext(_offset + now, Math.Clamp(now - _last, 0, 0.25)), _points, _colors);
            _last = now;
        }
        else if (_stale)
        {
            // Run up to the still frame in small steps, so effects that build up over time look right.
            for (double t = 0; t <= _offset; t += 1 / 15.0)
                _effect.Render(new FrameContext(t, 1 / 15.0), _points, _colors);
        }
        _stale = false;

        int cols = Math.Max(2, Columns), rows = Math.Max(1, Rows);
        double cellW = bounds.Width / cols, cellH = bounds.Height / rows;
        double r = Math.Max(1, Math.Min(cellW, cellH) * 0.36);
        for (int i = 0; i < _points.Length; i++)
        {
            var c = _colors[i];
            if (Math.Max(c.R, Math.Max(c.G, c.B)) < 12) continue;
            var center = new Point((i % cols + 0.5) * cellW, (i / cols + 0.5) * cellH);
            ctx.DrawEllipse(new ImmutableSolidColorBrush(Color.FromArgb(70, c.R, c.G, c.B)), null, center, r * 1.9, r * 1.9);
            ctx.DrawEllipse(new ImmutableSolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), null, center, r, r);
        }
    }
}
