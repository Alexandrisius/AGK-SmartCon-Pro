using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SmartCon.PipeConnect.Converters;

/// <summary>
/// Локальный конвертер bool → Visibility для PipeConnect.
/// true = Visible, false = Collapsed.
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}
