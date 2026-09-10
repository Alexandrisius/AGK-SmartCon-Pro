using System;
using System.Globalization;
using System.Windows.Data;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.UI.Converters;

/// <summary>
/// Converts <see cref="TypePresenceState"/> (#187) to a localized tooltip
/// for the presence dot in the catalog tree.
/// </summary>
public sealed class PresenceStateToTooltipConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not TypePresenceState state) return null;
        return StatusTooltipText.ForPresenceState(state);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
