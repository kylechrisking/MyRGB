using Avalonia;
using Avalonia.Controls.Primitives;

namespace MyRGB.App.Controls;

/// <summary>
/// MyRGB's on/off switch: a ToggleButton with a label for each state, drawn by Themes/Toggles.axaml in
/// whatever shape the theme wants (pill, tick box, DIP switch, [x]).
/// It replaces Avalonia's ToggleSwitch, whose OnApplyTemplate demands two named parts and threw
/// "Could not find control 'PART_MovingKnobs'" when a theme change re-templated it on screen. That
/// crashed 0.6.2 on 2026-11-18 when Match Windows switched to Notebook at sunrise.
/// </summary>
public class Switch : ToggleButton
{
    public static readonly StyledProperty<object?> OnContentProperty = AvaloniaProperty.Register<Switch, object?>(nameof(OnContent));
    public static readonly StyledProperty<object?> OffContentProperty = AvaloniaProperty.Register<Switch, object?>(nameof(OffContent));

    /// <summary>The label while it's on.</summary>
    public object? OnContent
    {
        get => GetValue(OnContentProperty);
        set => SetValue(OnContentProperty, value);
    }

    /// <summary>The label while it's off.</summary>
    public object? OffContent
    {
        get => GetValue(OffContentProperty);
        set => SetValue(OffContentProperty, value);
    }
}
