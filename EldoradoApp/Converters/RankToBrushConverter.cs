using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using EldoradoApp.Models;

namespace EldoradoApp.Converters;

public sealed class RankToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var rank = value as string ?? "Iron";
        var color = (Color)ColorConverter.ConvertFromString(ValorantRanks.ColorHex(rank))!;
        return new SolidColorBrush(color);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
