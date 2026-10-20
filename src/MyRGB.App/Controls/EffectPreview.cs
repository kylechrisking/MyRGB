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
/// A small live sample of one effect with its default settings, drawn as a grid of LEDs, so an
/// effect can be picked by how it looks. Runs its own copy of the effect; the hardware isn't touched.
/// </summary>
public sealed class EffectPreview : Control
{
    public static readonly StyledProperty<EffectDefinition?> DefinitionProperty =
        AvaloniaProperty.Register<EffectPreview, EffectDefinition?>(nameof(Definition));

    private const int Columns = 22, Rows = 8;
    private static readonly IBrush Background = new ImmutableSolidColorBrush(Color.FromRgb(4, 14, 19));

    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private readonly LedPoint[] _points = new LedPoint[Columns * Rows];
    private readonly Rgb[] _colors = new Rgb[Columns * Rows];
    private MyRGB.Core.Effects.Effect? _effect;
    private double _last;

    public EffectPreview()
    {
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Columns; c++)
                _points[r * Columns + c] = new LedPoint(c / (float)(Columns - 1), r / (float)(Rows - 1));
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, (_, _) => InvalidateVisual());
        ClipToBounds = true;
    }

    public EffectDefinition? Definition
    {
        get => GetValue(DefinitionProperty);
        set => SetValue(DefinitionProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DefinitionProperty) _effect = Definition?.Create(EffectSettings.Empty);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _clock.Start();
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
        _clock.Stop();
    }

    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        ctx.DrawRectangle(Background, null, bounds, 7, 7);
        if (_effect is null) return;

        double now = _clock.Elapsed.TotalSeconds;
        _effect.Render(new FrameContext(now, Math.Clamp(now - _last, 0, 0.25)), _points, _colors);
        _last = now;

        double cellW = bounds.Width / Columns, cellH = bounds.Height / Rows;
        double r = Math.Max(1, Math.Min(cellW, cellH) * 0.36);
        for (int i = 0; i < _points.Length; i++)
        {
            var c = _colors[i];
            if (Math.Max(c.R, Math.Max(c.G, c.B)) < 12) continue;
            var center = new Point((i % Columns + 0.5) * cellW, (i / Columns + 0.5) * cellH);
            ctx.DrawEllipse(new ImmutableSolidColorBrush(Color.FromArgb(70, c.R, c.G, c.B)), null, center, r * 1.9, r * 1.9);
            ctx.DrawEllipse(new ImmutableSolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), null, center, r, r);
        }
    }
}
