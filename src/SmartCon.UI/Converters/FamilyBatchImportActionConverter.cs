using System.Globalization;
using System.Windows.Data;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.UI;

namespace SmartCon.UI.Converters;

[ValueConversion(typeof(FamilyBatchImportAction), typeof(string))]
public sealed class FamilyBatchImportActionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not FamilyBatchImportAction action)
            return value?.ToString() ?? string.Empty;

        return action switch
        {
            FamilyBatchImportAction.IncrementVersion => LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_ActionIncrement) ?? "New Version",
            FamilyBatchImportAction.OverwriteCurrent => LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_ActionOverwrite) ?? "Overwrite Current",
            FamilyBatchImportAction.Skip => LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_ActionSkip) ?? "Skip",
            _ => action.ToString()
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
