using System.Globalization;
using System.Windows.Data;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.UI;

namespace SmartCon.UI.Converters;

[ValueConversion(typeof(FamilyBatchImportStatus), typeof(string))]
public sealed class FamilyBatchImportStatusConverter : IValueConverter, IMultiValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not FamilyBatchImportStatus status)
            return value?.ToString() ?? string.Empty;

        return StatusToString(status, null);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is null || values.Length == 0)
            return string.Empty;

        var status = values[0] is FamilyBatchImportStatus s ? s : FamilyBatchImportStatus.New;
        var versionLabel = values.Length > 1 ? values[1] as string : null;
        // #180: third binding — the matched version came from the verified
        // ES marker, not from content-hash dedup; annotate it so
        // "Duplicate (v2)" is not misread as "content-identical to v2".
        var isMarkerResolved = values.Length > 2 && values[2] is bool b && b;

        return StatusToString(status, versionLabel, isMarkerResolved);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    private static string StatusToString(FamilyBatchImportStatus status, string? versionLabel, bool isMarkerResolved = false)
    {
        var baseText = status switch
        {
            FamilyBatchImportStatus.New => LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_StatusNew) ?? "New",
            FamilyBatchImportStatus.Existing => LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_StatusExisting) ?? "Existing",
            FamilyBatchImportStatus.Duplicate => LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_StatusDuplicate) ?? "Duplicate",
            FamilyBatchImportStatus.Error => LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_StatusError) ?? "Error",
            _ => status.ToString()
        };

        if (status == FamilyBatchImportStatus.Duplicate && !string.IsNullOrWhiteSpace(versionLabel))
        {
            var markerSuffix = isMarkerResolved
                ? $" — {LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_StatusMarkerSuffix) ?? "marker"}"
                : string.Empty;
            return $"{baseText} ({versionLabel}{markerSuffix})";
        }

        return baseText;
    }
}
