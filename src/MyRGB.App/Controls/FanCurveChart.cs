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
/// A fan curve: temperature across, fan speed up. Drag a point up or down to change its speed.
/// Shows the safety floor, the emergency line and where the temperature is right now.
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
    private static readonly IBrush FloorBrush = new ImmutableSolidColorBrush(Color.FromArgb(22, 255, 255, 255));
    private static readonly IPen EmergencyPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(150, 247, 93, 89)), 1, new ImmutableDashStyle([4, 4], 0));
    private static readonly IPen LinePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(86, 219, 143)), 2.2, lineJoin: PenLineJoin.Round);
    private static readonly IBrush PointBrush = new ImmutableSolidColorBrush(Color.FromRgb(226, 234, 229));
    private static readonly IPen NowPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(200, 66, 163, 253)), 1.5);
    private static readonly IBrush LabelBrush = new ImmutableSolidColorBrush(Color.FromRgb(130, 146, 152));
    private static readonly Typeface Face = new(new FontFamily("avares://MyRGB/Assets/Fonts#Manrope"));

    private int _dragging = -1;

    static FanCurveChart() => AffectsRender<FanCurveChart>(PointsProperty, CurrentTempProperty);

    public FanCurveChart() => Cursor = new Cursor(StandardCursorType.Hand);

    public IReadOnlyList<CurvePoint>? Points { get => GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public double? CurrentTemp { get => GetValue(CurrentTempProperty); set => SetValue(CurrentTempProperty, value); }

    private Rect Plot => new(PadLeft, PadTop, Math.Max(1, Bounds.Width - PadLeft - PadRight), Math.Max(1, Bounds.Height - PadTop - PadBottom));
    private Point ToScreen(double temp, double percent) =>
        new(Plot.X + (temp - MinTemp) / (MaxTemp - MinTemp) * Plot.Width, Plot.Bottom - percent / 100 * Plot.Height);

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

        // Below the floor the fan never goes; past the emergency line it always runs flat out.
        var floorTop = ToScreen(MinTemp, FanController.Floor).Y;
        ctx.DrawRectangle(FloorBrush, null, new Rect(plot.Left, floorTop, plot.Width, plot.Bottom - floorTop));
        var ex = ToScreen(FanController.EmergencyTemp, 0).X;
        ctx.DrawLine(EmergencyPen, new Point(ex, plot.Top), new Point(ex, plot.Bottom));

        if (Points is not { Count: > 0 } points) return;
        var sorted = points.OrderBy(p => p.Temp).ToList();
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(ToScreen(MinTemp, Math.Max(FanController.Floor, sorted[0].Percent)), false);
            foreach (var p in sorted) g.LineTo(ToScreen(p.Temp, Math.Max(FanController.Floor, p.Percent)));
            g.LineTo(ToScreen(FanController.EmergencyTemp, Math.Max(FanController.Floor, sorted[^1].Percent)));
            g.LineTo(ToScreen(FanController.EmergencyTemp, 100));
            g.LineTo(ToScreen(MaxTemp, 100));
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, LinePen, geo);
        foreach (var p in sorted)
            ctx.DrawEllipse(PointBrush, null, ToScreen(p.Temp, Math.Max(FanController.Floor, p.Percent)), 5, 5);

        if (CurrentTemp is { } now && now is >= MinTemp and <= MaxTemp)
        {
            var x = ToScreen(now, 0).X;
            ctx.DrawLine(NowPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            Label(ctx, $"now {now:0}°", new Point(Math.Min(x + 4, plot.Right - 46), plot.Top + 2));
        }
    }

    private static void Label(DrawingContext ctx, string text, Point at) =>
        ctx.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 10, LabelBrush), at);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Points is not { } points) return;
        var pos = e.GetPosition(this);
        for (int i = 0; i < points.Count; i++)
        {
            var p = ToScreen(points[i].Temp, Math.Max(FanController.Floor, points[i].Percent));
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
        double percent = Math.Clamp((Plot.Bottom - e.GetPosition(this).Y) / Plot.Height * 100, FanController.Floor, 100);
        var next = points.ToList();
        // Keep the curve rising: a point can't go below the one before it or above the one after.
        var sorted = next.Select((p, i) => (p, i)).OrderBy(x => x.p.Temp).ToList();
        int rank = sorted.FindIndex(x => x.i == _dragging);
        double low = rank > 0 ? sorted[rank - 1].p.Percent : FanController.Floor;
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
