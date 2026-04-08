using System.Globalization;
using System.Windows.Data;

namespace SmartCon.UI.Converters;

[ValueConversion(typeof(bool), typeof(bool))]
public sealed class InvertBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b ? !b : true;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is bool b ? !b : true;
    }
}
