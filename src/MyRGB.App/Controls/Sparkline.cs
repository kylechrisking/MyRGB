using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MyRGB.App.Controls;

/// <summary>
/// A small line chart of recent readings: a line with a soft fill under it. Gaps (NaN) break the
/// line. The scale is <see cref="Minimum"/>..<see cref="Maximum"/>, or fitted to the data when
/// Maximum is 0.
/// </summary>
public sealed class Sparkline : Control
{
    public static readonly StyledProperty<float[]?> ValuesProperty = AvaloniaProperty.Register<Sparkline, float[]?>(nameof(Values));
    public static readonly StyledProperty<Color> ColorProperty = AvaloniaProperty.Register<Sparkline, Color>(nameof(Color), Color.FromRgb(86, 219, 143));
    public static readonly StyledProperty<double> MinimumProperty = AvaloniaProperty.Register<Sparkline, double>(nameof(Minimum));
    public static readonly StyledProperty<double> MaximumProperty = AvaloniaProperty.Register<Sparkline, double>(nameof(Maximum));

    static Sparkline() => AffectsRender<Sparkline>(ValuesProperty, ColorProperty, MinimumProperty, MaximumProperty);

    public float[]? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Color Color { get => GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    public override void Render(DrawingContext ctx)
    {
        var values = Values;
        var size = Bounds.Size;
        if (values is null || values.Length < 2 || size.Width <= 0 || size.Height <= 0) return;

        double min = Minimum, max = Maximum;
        if (max <= min)
        {
            var real = values.Where(v => !float.IsNaN(v)).ToArray();
            if (real.Length == 0) return;
            min = Math.Min(Minimum, real.Min());
            max = Math.Max(real.Max() * 1.15, min + 1);
        }

        double step = size.Width / (SensorHubLength - 1), offset = size.Width - (values.Length - 1) * step;
        Point At(int i) => new(offset + i * step, size.Height - 1 - Math.Clamp((values[i] - min) / (max - min), 0, 1) * (size.Height - 2));

        var line = new StreamGeometry();
        var fill = new StreamGeometry();
        using (var l = line.Open())
        using (var f = fill.Open())
        {
            bool open = false;
            for (int i = 0; i < values.Length; i++)
            {
                if (float.IsNaN(values[i]))
                {
                    if (open) { Close(f, i - 1); l.EndFigure(false); open = false; }
                    continue;
                }
                var p = At(i);
                if (!open)
                {
                    l.BeginFigure(p, false);
                    f.BeginFigure(new Point(p.X, size.Height), true);
                    f.LineTo(p);
                    open = true;
                }
                else
                {
                    l.LineTo(p);
                    f.LineTo(p);
                }
            }
            if (open) { Close(f, values.Length - 1); l.EndFigure(false); }
        }

        var c = Color;
        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(90, c.R, c.G, c.B), 0), new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1) },
        };
        ctx.DrawGeometry(gradient, null, fill);
        ctx.DrawGeometry(null, new Pen(new SolidColorBrush(c), 1.6, lineJoin: PenLineJoin.Round), line);

        void Close(StreamGeometryContext f, int last)
        {
            f.LineTo(new Point(At(last).X, size.Height));
            f.EndFigure(true);
        }
    }

    /// <summary>Charts always span the hub's full history, so a fresh chart fills in from the right.</summary>
    private const int SensorHubLength = MyRGB.Core.Sensors.SensorHub.HistoryLength;
}
