using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using MyRGB.Core;
using MyRGB.Core.Effects;

namespace MyRGB.App.ViewModels;

/// <summary>One editor row in the Studio, built from an effect's <see cref="ParameterSpec"/>.</summary>
public abstract class ParameterViewModel(ParameterSpec spec, Action<string, string> changed) : ObservableObject
{
    public string Key => spec.Key;
    public string Label => spec.Label;

    protected void Push(string value) => changed(spec.Key, value);

    public static ParameterViewModel Create(ParameterSpec spec, string value, Action<string, string> changed) => spec switch
    {
        ColorParameter c => new ColorParameterViewModel(c, value, changed),
        NumberParameter n => new NumberParameterViewModel(n, value, changed),
        ChoiceParameter ch => new ChoiceParameterViewModel(ch, value, changed),
        BoolParameter b => new BoolParameterViewModel(b, value, changed),
        _ => throw new NotSupportedException(spec.GetType().Name),
    };
}

public sealed partial class ColorParameterViewModel : ParameterViewModel
{
    public ColorParameterViewModel(ColorParameter spec, string value, Action<string, string> changed) : base(spec, changed)
    {
        var c = Rgb.TryParse(value, out var parsed) ? parsed : spec.Default;
        _color = Avalonia.Media.Color.FromRgb(c.R, c.G, c.B);
    }

    [ObservableProperty]
    private Color _color;

    partial void OnColorChanged(Color value) => Push(new Rgb(value.R, value.G, value.B).ToHex());
}

public sealed partial class NumberParameterViewModel : ParameterViewModel
{
    public NumberParameterViewModel(NumberParameter spec, string value, Action<string, string> changed) : base(spec, changed)
    {
        Min = spec.Min;
        Max = spec.Max;
        Step = spec.Step;
        _value = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : spec.Default;
    }

    public double Min { get; }
    public double Max { get; }
    public double Step { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Display))]
    private double _value;

    public string Display => Step < 1 ? Value.ToString("0.##", CultureInfo.CurrentCulture) : Math.Round(Value).ToString(CultureInfo.CurrentCulture);

    partial void OnValueChanged(double value) => Push(value.ToString(CultureInfo.InvariantCulture));
}

public sealed partial class ChoiceParameterViewModel : ParameterViewModel
{
    public ChoiceParameterViewModel(ChoiceParameter spec, string value, Action<string, string> changed) : base(spec, changed)
    {
        Options = spec.Options;
        _selected = spec.Options.Contains(value) ? value : spec.Default;
    }

    public IReadOnlyList<string> Options { get; }

    [ObservableProperty]
    private string _selected;

    partial void OnSelectedChanged(string value) => Push(value);
}

public sealed partial class BoolParameterViewModel : ParameterViewModel
{
    public BoolParameterViewModel(BoolParameter spec, string value, Action<string, string> changed) : base(spec, changed) =>
        _value = value.Equals("true", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty]
    private bool _value;

    partial void OnValueChanged(bool value) => Push(value ? "true" : "false");
}
