using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using MyRGB.Core.Localization;

namespace MyRGB.App.Localization;

/// <summary>
/// Words on screen in the user's language: <c>Text="{l:Tr home.title}"</c>.
/// The text is looked up once, when the page is built; a language change takes a restart.
/// </summary>
public sealed class TrExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override string ProvideValue(IServiceProvider serviceProvider) => Strings.Get(Key);
}

/// <summary>For a value inside a sentence: the key's text with {0} filled in by the bound value.</summary>
/// <example><c>Text="{Binding Brightness, Converter={x:Static l:Tr.Format}, ConverterParameter=top.brightness}"</c></example>
public static class Tr
{
    public static readonly IValueConverter Format = new FormatConverter();

    private sealed class FormatConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            parameter is string key ? Strings.Format(key, value) : value;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
