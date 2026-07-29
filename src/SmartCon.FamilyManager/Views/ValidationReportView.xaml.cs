using SmartCon.FamilyManager.ViewModels;
using SmartCon.UI;
using SmartCon.UI.Controls;

namespace SmartCon.FamilyManager.Views;

public sealed partial class ValidationReportView : DialogWindowBase
{
    public ValidationReportView(ValidationReportViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        BindCloseRequest(viewModel);

        ColSeverity.Header = string.Empty;
        ColType.Header = LanguageManager.GetString(StringLocalization.Keys.FM_ValidationReport_ColType);
        ColAttribute.Header = LanguageManager.GetString(StringLocalization.Keys.FM_ValidationReport_ColAttribute);
        ColCheck.Header = LanguageManager.GetString(StringLocalization.Keys.FM_ValidationReport_ColCheck);
        ColExpected.Header = LanguageManager.GetString(StringLocalization.Keys.FM_ValidationReport_ColExpected);
        ColActual.Header = LanguageManager.GetString(StringLocalization.Keys.FM_ValidationReport_ColActual);
    }
}
