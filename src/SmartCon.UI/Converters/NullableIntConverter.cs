using System.Globalization;
using System.Windows.Data;

namespace SmartCon.UI.Converters;

[ValueConversion(typeof(int?), typeof(string))]
public sealed class NullableIntConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is int i ? i.ToString(culture) : string.Empty;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s))
            return null;

        return int.TryParse(s, NumberStyles.Integer, culture, out var result) ? result : null;
    }
}
