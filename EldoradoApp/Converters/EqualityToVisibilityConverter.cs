using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace EldoradoApp.Converters;

/// <summary>
/// Visible when the bound value equals the converter parameter, Collapsed otherwise.
/// Used by the shell so every page stays alive (the embedded browser must keep its
/// session) while only the selected one is shown.
/// </summary>
public sealed class EqualityToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var left = value?.ToString();
        var right = parameter?.ToString();
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
