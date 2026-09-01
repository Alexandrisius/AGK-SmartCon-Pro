using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SmartCon.UI.Converters;

/// <summary>
/// Конвертер bool -> GridLength для WPF-биндингов.
/// true = фиксированная ширина из ConverterParameter (пиксели, по умолчанию 100),
/// false = 0 (колонка схлопывается). Используется для скрытия фиксированных
/// колонок Мин/Макс трассировки у не-труб (ADR-072).
/// </summary>
[ValueConversion(typeof(bool), typeof(GridLength))]
public sealed class BoolToGridLengthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var pixels = parameter is string s
            && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 100.0;
        return value is true ? new GridLength(pixels) : new GridLength(0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
