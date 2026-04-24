using System.ComponentModel;
using SmartCon.Core.Services;
using SmartCon.ProjectManagement.ViewModels;
using SmartCon.UI;
using SmartCon.UI.Controls;

namespace SmartCon.ProjectManagement.Views;

public partial class FieldLibraryView : DialogWindowBase
{
    public FieldLibraryView(FieldLibraryViewModel viewModel)
    {
        InitializeComponent();
        LanguageManager.EnsureWindowResources(this);
        DataContext = viewModel;

        ColFieldName.Header = LocalizationService.GetString("PM_Col_FieldName");
        ColDisplayName.Header = LocalizationService.GetString("PM_Col_DisplayName");
        ColDescription.Header = LocalizationService.GetString("PM_Col_Description");
        ColValidation.Header = LocalizationService.GetString("PM_Col_Validation");
        ColMinLen.Header = LocalizationService.GetString("PM_Col_Min");
        ColMaxLen.Header = LocalizationService.GetString("PM_Col_Max");

        viewModel.SetOwnerWindow(this);
        BindCloseRequest(viewModel);
    }

    protected override void OnUserInitiatedClose(CancelEventArgs e)
    {
        CustomDialogResult = false;
    }
}
