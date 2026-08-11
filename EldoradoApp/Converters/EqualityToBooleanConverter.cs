using System.Globalization;
using System.Windows.Data;

namespace EldoradoApp.Converters;

/// <summary>True when the bound value equals the converter parameter (nav highlighting, radio groups).</summary>
public sealed class EqualityToBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is not null ? parameter : Binding.DoNothing;
}
