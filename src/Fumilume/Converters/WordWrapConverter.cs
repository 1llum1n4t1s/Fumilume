using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Fumilume.Converters;

public sealed class WordWrapConverter : IValueConverter
{
    public static WordWrapConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? TextWrapping.Wrap : TextWrapping.NoWrap;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
