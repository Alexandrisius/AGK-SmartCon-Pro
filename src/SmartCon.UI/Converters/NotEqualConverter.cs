using System.Globalization;
using System.Windows.Data;

namespace SmartCon.UI.Converters;

[ValueConversion(typeof(object), typeof(bool))]
public sealed class NotEqualConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null)
            return !Equals(value, parameter);

        if (value is Enum enumValue && parameter is string paramString)
        {
            try
            {
                var parsed = Enum.Parse(enumValue.GetType(), paramString, ignoreCase: true);
                return !enumValue.Equals(parsed);
            }
            catch { }
        }

        return !value.Equals(parameter);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
