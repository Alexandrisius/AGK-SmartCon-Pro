using SmartCon.Core.Services;
using SmartCon.ProjectManagement.ViewModels;
using SmartCon.UI;
using SmartCon.UI.Controls;

namespace SmartCon.ProjectManagement.Views;

public partial class ShareSettingsView : DialogWindowBase
{
    public ShareSettingsView(ShareSettingsViewModel viewModel)
    {
        InitializeComponent();
        LanguageManager.EnsureWindowResources(this);
        DataContext = viewModel;

        ColIndex.Header = LocalizationService.GetString("PM_Col_Index");
        ColField.Header = LocalizationService.GetString("PM_Col_Field");
        ColParseRule.Header = LocalizationService.GetString("PM_Col_ParseRule");
        ColValue.Header = LocalizationService.GetString("PM_Col_Value");
        ColStatus.Header = LocalizationService.GetString("PM_Col_ValidationCheck");
        ColMapField.Header = LocalizationService.GetString("PM_Col_Field");
        ColSource.Header = LocalizationService.GetString("PM_Col_SourceValue");
        ColTarget.Header = LocalizationService.GetString("PM_Col_TargetValue");
        ColMapStatus.Header = LocalizationService.GetString("PM_Col_ValidationCheck");

        viewModel.SetOwnerWindow(this);
        BindCloseRequest(viewModel);
    }
}
