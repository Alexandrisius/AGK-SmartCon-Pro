using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using SmartCon.Core.Logging;

namespace SmartCon.UI.Converters;

/// <summary>
/// Converts bool IsStale to foreground brush. True = WarningBrush (orange), False = TextPrimaryBrush.
/// </summary>
[ValueConversion(typeof(bool), typeof(Brush))]
public sealed class BoolToStaleForegroundConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isStale = value is bool b && b;
        if (isStale)
        {
            // WarningBrush - orange color for stale families
            return new SolidColorBrush(Color.FromRgb(255, 152, 0)); // #FF9800
        }
        // TextPrimaryBrush - default dark text
        return new SolidColorBrush(Color.FromRgb(51, 51, 51)); // #333333
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}