using System;
using System.Globalization;
using System.Windows.Data;

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
        return StatusTooltipText.ForStaleCount(count);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
