using System.Globalization;
using System.Windows.Data;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.UI;

namespace SmartCon.UI.Converters;

[ValueConversion(typeof(FamilyBatchImportStatus), typeof(string))]
public sealed class FamilyBatchImportStatusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not FamilyBatchImportStatus status)
            return value?.ToString() ?? string.Empty;

        return status switch
        {
            FamilyBatchImportStatus.New => LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_StatusNew) ?? "New",
            FamilyBatchImportStatus.Existing => LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_StatusExisting) ?? "Existing",
            FamilyBatchImportStatus.Error => LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_StatusError) ?? "Error",
            _ => status.ToString()
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
