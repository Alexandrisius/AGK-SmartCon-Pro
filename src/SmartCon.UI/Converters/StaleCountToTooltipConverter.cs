using System;
using System.Globalization;
using System.Windows.Data;
using SmartCon.Core.Services;
using SmartCon.UI;

namespace SmartCon.UI.Converters;

/// <summary>
/// Converts an integer stale count to a localized tooltip string.
/// Used on the roll-up <c>⚠</c> indicator on category nodes (Phase 24 / ADR-030).
/// </summary>
public sealed class StaleCountToTooltipConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not int count) return null;
        if (count <= 0) return null;

        var key = count == 1
            ? StringLocalization.Keys.FM_StaleCategoryTooltipOne
            : StringLocalization.Keys.FM_StaleCategoryTooltipMany;
        return count == 1
            ? LanguageManager.GetString(key)
            : LocalizationService.Format(key, count);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
