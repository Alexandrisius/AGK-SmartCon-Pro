using System.Globalization;
using System.Windows.Data;

namespace SmartCon.UI.Converters;

[ValueConversion(typeof(object), typeof(string))]
public sealed class TypeCountToDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value?.ToString() ?? "—";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
