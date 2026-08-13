using System;
using System.Globalization;
using System.Windows.Data;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.UI.Converters;

/// <summary>
/// Converts <see cref="StaleReason"/> to a localized tooltip string.
/// </summary>
public sealed class StaleReasonToTooltipConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not StaleReason reason) return null;
        return StatusTooltipText.ForStaleReason(reason);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
