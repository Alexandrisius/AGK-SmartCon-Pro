using System;
using System.Globalization;
using System.Windows.Data;
using SmartCon.Core.Services;

namespace SmartCon.UI.Converters;

/// <summary>
/// Converts the total family count to a localized tooltip string.
/// Used on the counter badge in the status bar of the FamilyManager panel.
/// </summary>
public sealed class TotalItemCountToTooltipConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not int count) return null;

        var key = StringLocalization.Keys.FM_TotalFamiliesTooltip;
        return LocalizationService.Format(key, count);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}