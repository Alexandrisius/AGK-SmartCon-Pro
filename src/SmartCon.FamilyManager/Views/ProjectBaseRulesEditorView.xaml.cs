using SmartCon.Core.Services;
using SmartCon.FamilyManager.ViewModels.ProjectBase;
using SmartCon.UI.Controls;

namespace SmartCon.FamilyManager.Views;

public sealed partial class ProjectBaseRulesEditorView : DialogWindowBase
{
    public ProjectBaseRulesEditorView(ProjectBaseRulesEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        BindCloseRequest(viewModel);

        ColIndex.Header = LocalizationService.GetString("FM_PBase_ColIndex");
        ColField.Header = LocalizationService.GetString("FM_PBase_ColField");
        ColParseRule.Header = LocalizationService.GetString("FM_PBase_ColParseRule");
        ColValue.Header = LocalizationService.GetString("FM_PBase_ColValue");
        ColStatus.Header = LocalizationService.GetString("FM_PBase_ColStatus");
    }
}
