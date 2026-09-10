using System;
using System.Globalization;
using System.Windows.Data;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.UI.Converters;

/// <summary>
/// Converts (FamilyUnavailableReason, requiredRevitVersion, currentRevitVersion,
/// hasCompatibleVersion) to a localized tooltip explaining WHY the family is
/// unavailable for loading and WHAT the user can do about it.
/// </summary>
public sealed class UnavailableReasonToTooltipConverter : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 4 || values[0] is not FamilyUnavailableReason reason || reason == FamilyUnavailableReason.None)
            return null;

        var required = ToInt(values[1]);
        var current = ToInt(values[2]);
        var hasCompatible = values[3] is bool b && b;

        var deprecatedText = LanguageManager.GetString(StringLocalization.Keys.FM_UnavailableTooltipDeprecated)
            ?? "Family is deprecated — loading into the project is not allowed.";

        string? revitText = null;
        if (reason is FamilyUnavailableReason.RevitVersion or FamilyUnavailableReason.DeprecatedAndRevitVersion)
        {
            var key = hasCompatible
                ? StringLocalization.Keys.FM_UnavailableTooltipRevitFix
                : StringLocalization.Keys.FM_UnavailableTooltipRevitNone;
            var template = LanguageManager.GetString(key)
                ?? "Saved in Revit {0} — cannot be loaded into Revit {1}.";
            revitText = string.Format(CultureInfo.CurrentCulture, template, required, current);
        }

        return reason switch
        {
            FamilyUnavailableReason.Deprecated => deprecatedText,
            FamilyUnavailableReason.RevitVersion => revitText,
            FamilyUnavailableReason.DeprecatedAndRevitVersion =>
                (LanguageManager.GetString(StringLocalization.Keys.FM_UnavailableTooltipDeprecatedShort)
                    ?? "Family is deprecated — loading into the project is not allowed.")
                + Environment.NewLine + revitText,
            _ => null,
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static int ToInt(object? value) => value switch
    {
        int i => i,
        long l => (int)l,
        _ => 0,
    };
}
