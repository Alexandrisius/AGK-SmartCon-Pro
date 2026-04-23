using SmartCon.Core.Models;
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
        ColRole.Header = LocalizationService.GetString("PM_Col_Role");
        ColLabel.Header = LocalizationService.GetString("PM_Col_Label");
        ColWip.Header = LocalizationService.GetString("PM_Col_Wip");
        ColShared.Header = LocalizationService.GetString("PM_Col_Shared");

        ColRole.ItemsSource = FileBlockDefinition.PredefinedRoles;

        BindCloseRequest(viewModel);
    }
}
