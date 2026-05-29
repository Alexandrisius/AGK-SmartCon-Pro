using System.Globalization;
using System.Windows.Data;

namespace SmartCon.UI.Converters;

[ValueConversion(typeof(long), typeof(string))]
public sealed class FileSizeToMbConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not long bytes) return string.Empty;
        var mb = bytes / (1024.0 * 1024.0);
        return $"{mb:F1} MB";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
