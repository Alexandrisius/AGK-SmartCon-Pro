using System;
using System.Globalization;
using System.Windows.Data;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.UI;

namespace SmartCon.UI.Converters;

/// <summary>
/// Converts <see cref="StaleReason"/> to a localized tooltip string.
/// </summary>
public sealed class StaleReasonToTooltipConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not StaleReason reason) return null;

        var key = reason switch
        {
            StaleReason.NoEntityStorage => StringLocalization.Keys.FM_StaleTooltipNoES,
            StaleReason.VersionMismatch => StringLocalization.Keys.FM_StaleTooltipMismatch,
            StaleReason.RevitVersionMismatch => StringLocalization.Keys.FM_StaleTooltipRevitVer,
            StaleReason.NotInCatalog => StringLocalization.Keys.FM_StaleTooltipNotInCatalog,
            StaleReason.ContentDrift => StringLocalization.Keys.FM_StaleTooltipContentDrift,
            _ => StringLocalization.Keys.FM_StaleTooltipNone,
        };
        return LanguageManager.GetString(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
