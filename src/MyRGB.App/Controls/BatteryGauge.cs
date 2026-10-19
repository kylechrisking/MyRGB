using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MyRGB.App.Controls;

/// <summary>A small battery glyph: fill shows the level, red/amber/green by charge, a bolt while charging.</summary>
public sealed class BatteryGauge : Control
{
    public static readonly StyledProperty<int> PercentProperty = AvaloniaProperty.Register<BatteryGauge, int>(nameof(Percent));
    public static readonly StyledProperty<bool> ChargingProperty = AvaloniaProperty.Register<BatteryGauge, bool>(nameof(Charging));

    static BatteryGauge() => AffectsRender<BatteryGauge>(PercentProperty, ChargingProperty);

    public BatteryGauge()
    {
        Width = 26;
        Height = 13;
    }

    public int Percent
    {
        get => GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    public bool Charging
    {
        get => GetValue(ChargingProperty);
        set => SetValue(ChargingProperty, value);
    }

    public static Color LevelColor(int percent, bool charging) =>
        charging ? Color.FromRgb(74, 186, 244)
        : percent < 20 ? Color.FromRgb(247, 93, 89)
        : percent < 45 ? Color.FromRgb(252, 180, 66)
        : Color.FromRgb(86, 219, 143);

    public override void Render(DrawingContext ctx)
    {
        var b = Bounds.Size;
        double nub = 2.5, w = b.Width - nub, h = b.Height;
        var color = LevelColor(Percent, Charging);
        var outline = new Pen(new SolidColorBrush(Color.FromArgb(200, 188, 199, 199)), 1.2);

        ctx.DrawRectangle(null, outline, new Rect(0.6, 0.6, w - 1.2, h - 1.2), 3, 3);
        ctx.DrawRectangle(outline.Brush, null, new Rect(w, h * 0.32, nub, h * 0.36), 1, 1);

        double fill = Math.Clamp(Percent, 0, 100) / 100.0 * (w - 5);
        if (fill > 0.5)
            ctx.DrawRectangle(new SolidColorBrush(color), null, new Rect(2.5, 2.5, fill, h - 5), 1.5, 1.5);

        if (Charging)
        {
            var bolt = StreamGeometry.Parse($"M{w * 0.55},{h * 0.12} L{w * 0.33},{h * 0.55} H{w * 0.5} L{w * 0.42},{h * 0.9} L{w * 0.68},{h * 0.42} H{w * 0.52} Z");
            ctx.DrawGeometry(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(7, 17, 23)), 0.8), bolt);
        }
    }
}
