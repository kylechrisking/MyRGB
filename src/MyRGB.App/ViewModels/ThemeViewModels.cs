using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyRGB.App.Services;

namespace MyRGB.App.ViewModels;

/// <summary>One card in Settings' Appearance picker, with a little drawing of the theme on it.</summary>
public sealed partial class ThemeOptionViewModel(ThemeInfo theme, Action<string> pick) : ObservableObject
{
    public string Id => theme.Id;
    public string Name => theme.Name;
    public string Description => theme.Description;
    public IBrush Page { get; } = Brush.Parse(theme.Preview[0]);
    public IBrush Surface { get; } = Brush.Parse(theme.Preview[1]);
    public IBrush Ink { get; } = Brush.Parse(theme.Preview[2]);
    public IBrush Accent { get; } = Brush.Parse(theme.Preview[3]);
    public IBrush Edge { get; } = new SolidColorBrush(Color.Parse(theme.Preview[2]), 0.25);

    [ObservableProperty] private bool _isSelected;

    [RelayCommand]
    private void Pick() => pick(theme.Id);
}
