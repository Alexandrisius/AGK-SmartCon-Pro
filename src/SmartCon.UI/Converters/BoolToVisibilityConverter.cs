using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SmartCon.UI.Converters;

/// <summary>
/// Конвертер bool -> Visibility для WPF-биндингов.
/// true = Visible, false = Collapsed.
/// </summary>
[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var invert = parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase);
        if (value is bool boolValue)
        {
            return (boolValue ^ invert) ? Visibility.Visible : Visibility.Collapsed;
        }

        return invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var invert = parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase);
        if (value is Visibility visibility)
        {
            return (visibility == Visibility.Visible) ^ invert;
        }

        return invert;
    }
}
