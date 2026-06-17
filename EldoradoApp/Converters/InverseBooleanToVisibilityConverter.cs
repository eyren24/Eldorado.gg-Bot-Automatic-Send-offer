using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace EldoradoApp.Converters;

/// <summary>True → Collapsed, False → Visible. Used to show a warning only when a switch is off.</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}
