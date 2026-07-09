using SmartCon.Core.Services;
using SmartCon.FamilyManager.ViewModels.ProjectBase;
using SmartCon.UI.Controls;

namespace SmartCon.FamilyManager.Views;

public partial class FieldLibraryView : DialogWindowBase
{
    public FieldLibraryView(FieldLibraryViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        BindCloseRequest(viewModel);

        ColFieldName.Header = LocalizationService.GetString("FM_PBase_ColField");
        ColDisplayName.Header = LocalizationService.GetString("FM_PBase_ColDisplayName");
        ColDescription.Header = LocalizationService.GetString("FM_PBase_ColDescription");
        ColValidation.Header = LocalizationService.GetString("FM_PBase_ColValidation");
        ColMinLen.Header = LocalizationService.GetString("FM_PBase_MinLength");
        ColMaxLen.Header = LocalizationService.GetString("FM_PBase_MaxLength");
    }
}
