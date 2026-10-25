using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using MyRGB.Core.Sensors;

namespace MyRGB.App.Controls;

/// <summary>
/// A fan curve: temperature across, fan speed up. Drag a point up or down to change its speed,
/// double-click empty space to add one, right-click one to remove it. Shows the emergency line
/// and where the temperature is right now, with the speed the curve is asking for at that dot.
/// </summary>
public sealed class FanCurveChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<CurvePoint>?> PointsProperty =
        AvaloniaProperty.Register<FanCurveChart, IReadOnlyList<CurvePoint>?>(nameof(Points), defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<double?> CurrentTempProperty =
        AvaloniaProperty.Register<FanCurveChart, double?>(nameof(CurrentTemp));

    private const double MinTemp = 20, MaxTemp = 100, PadLeft = 34, PadBottom = 20, PadTop = 8, PadRight = 10, Hit = 12;

    private static readonly IBrush Background = new ImmutableSolidColorBrush(Color.FromRgb(4, 14, 19));
    private static readonly IPen GridPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(28, 255, 255, 255)), 1);
    private static readonly IPen EmergencyPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(150, 247, 93, 89)), 1, new ImmutableDashStyle([4, 4], 0));
    private static readonly IPen LinePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(86, 219, 143)), 2.2, lineJoin: PenLineJoin.Round);
    private static readonly IBrush PointBrush = new ImmutableSolidColorBrush(Color.FromRgb(226, 234, 229));
    private static readonly IBrush NowBrush = new ImmutableSolidColorBrush(Color.FromRgb(66, 163, 253));
    private static readonly IPen NowPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(200, 66, 163, 253)), 1.5);
    private static readonly IBrush LabelBrush = new ImmutableSolidColorBrush(Color.FromRgb(130, 146, 152));
    private static readonly IBrush NowLabelBrush = new ImmutableSolidColorBrush(Color.FromRgb(160, 205, 235));
    private static readonly Typeface Face = new(new FontFamily("avares://MyRGB/Assets/Fonts#Manrope"));

    private int _dragging = -1;

    static FanCurveChart() => AffectsRender<FanCurveChart>(PointsProperty, CurrentTempProperty);

    public FanCurveChart() => Cursor = new Cursor(StandardCursorType.Hand);

    public IReadOnlyList<CurvePoint>? Points { get => GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public double? CurrentTemp { get => GetValue(CurrentTempProperty); set => SetValue(CurrentTempProperty, value); }

    private Rect Plot => new(PadLeft, PadTop, Math.Max(1, Bounds.Width - PadLeft - PadRight), Math.Max(1, Bounds.Height - PadTop - PadBottom));
    private Point ToScreen(double temp, double percent) =>
        new(Plot.X + (temp - MinTemp) / (MaxTemp - MinTemp) * Plot.Width, Plot.Bottom - percent / 100 * Plot.Height);
    private double TempAt(Point p) => Math.Clamp(MinTemp + (p.X - Plot.X) / Plot.Width * (MaxTemp - MinTemp), MinTemp, MaxTemp);
    private double PercentAt(Point p) => Math.Clamp((Plot.Bottom - p.Y) / Plot.Height * 100, 0, 100);

    public override void Render(DrawingContext ctx)
    {
        var plot = Plot;
        ctx.DrawRectangle(Background, null, new Rect(Bounds.Size), 10, 10);

        for (int p = 0; p <= 100; p += 25)
        {
            var y = ToScreen(MinTemp, p).Y;
            ctx.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            Label(ctx, $"{p}%", new Point(4, y - 7));
        }
        for (int t = 20; t <= 100; t += 20)
        {
            var x = ToScreen(t, 0).X;
            ctx.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            Label(ctx, $"{t}°", new Point(x - 8, plot.Bottom + 3));
        }

        // Past the emergency line the fan always runs flat out, whatever the curve says.
        var ex = ToScreen(FanController.EmergencyTemp, 0).X;
        ctx.DrawLine(EmergencyPen, new Point(ex, plot.Top), new Point(ex, plot.Bottom));

        if (Points is not { Count: > 0 } points) return;
        var sorted = points.OrderBy(p => p.Temp).ToList();
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(ToScreen(MinTemp, sorted[0].Percent), false);
            foreach (var p in sorted) g.LineTo(ToScreen(p.Temp, p.Percent));
            g.LineTo(ToScreen(FanController.EmergencyTemp, sorted[^1].Percent));
            g.LineTo(ToScreen(FanController.EmergencyTemp, 100));
            g.LineTo(ToScreen(MaxTemp, 100));
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, LinePen, geo);
        foreach (var p in sorted)
            ctx.DrawEllipse(PointBrush, null, ToScreen(p.Temp, p.Percent), 5, 5);

        if (CurrentTemp is { } now && now is >= MinTemp and <= MaxTemp)
        {
            var x = ToScreen(now, 0).X;
            ctx.DrawLine(NowPen, new Point(x, plot.Top), new Point(x, plot.Bottom));

            // The speed the curve is asking for at this very moment, riding on the curve itself.
            double percent = Math.Clamp(FanCurve.Evaluate(sorted, now), 0, 100);
            var dot = ToScreen(now, percent);
            ctx.DrawEllipse(NowBrush, null, dot, 6, 6);
            Label(ctx, $"now {now:0}° · {percent:0}%", new Point(Math.Min(x + 6, plot.Right - 92), plot.Top + 2), NowLabelBrush);
        }
    }

    private static void Label(DrawingContext ctx, string text, Point at, IBrush? brush = null) =>
        ctx.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 10, brush ?? LabelBrush), at);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Points is not { } points) return;
        var pos = e.GetPosition(this);
        var properties = e.GetCurrentPoint(this).Properties;

        if (properties.IsRightButtonPressed)
        {
            // Right-click a point to remove it, as long as a curve keeps at least two.
            if (points.Count <= 2) return;
            int nearest = -1;
            double best = Hit * Hit;
            for (int i = 0; i < points.Count; i++)
            {
                var p = ToScreen(points[i].Temp, points[i].Percent);
                double dx = p.X - pos.X, dy = p.Y - pos.Y, d = dx * dx + dy * dy;
                if (d <= best) { best = d; nearest = i; }
            }
            if (nearest >= 0)
            {
                var without = points.ToList();
                without.RemoveAt(nearest);
                Points = without;
                e.Handled = true;
            }
            return;
        }

        if (e.ClickCount == 2)
        {
            // Double-click empty space to add a point there.
            var added = points.Append(new CurvePoint(Math.Round(TempAt(pos)), Math.Round(PercentAt(pos)))).OrderBy(p => p.Temp).ToList();
            Points = added;
            e.Handled = true;
            return;
        }

        for (int i = 0; i < points.Count; i++)
        {
            var p = ToScreen(points[i].Temp, points[i].Percent);
            if (Math.Abs(p.X - pos.X) <= Hit && Math.Abs(p.Y - pos.Y) <= Hit)
            {
                _dragging = i;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging < 0 || Points is not { } points) return;
        double percent = PercentAt(e.GetPosition(this));
        var next = points.ToList();
        // Keep the curve rising: a point can't go below the one before it or above the one after.
        var sorted = next.Select((p, i) => (p, i)).OrderBy(x => x.p.Temp).ToList();
        int rank = sorted.FindIndex(x => x.i == _dragging);
        double low = rank > 0 ? sorted[rank - 1].p.Percent : 0;
        double high = rank < sorted.Count - 1 ? sorted[rank + 1].p.Percent : 100;
        next[_dragging] = next[_dragging] with { Percent = Math.Round(Math.Clamp(percent, low, high)) };
        Points = next;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = -1;
        e.Pointer.Capture(null);
    }
}
