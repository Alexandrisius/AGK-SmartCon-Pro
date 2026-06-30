using SmartCon.FamilyManager.ViewModels;
using SmartCon.UI;
using SmartCon.UI.Controls;

namespace SmartCon.FamilyManager.Views;

public sealed partial class FamilyPropertiesView : DialogWindowBase
{
    public FamilyPropertiesView(FamilyPropertiesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        BindCloseRequest(viewModel);
        InitializeVersionGridHeaders();
    }

    /// <summary>
    /// Programmatic header installation for the Versions tab DataGrid columns
    /// (I-12: DataGridColumn.Header is not a FrameworkElement — DynamicResource
    /// won't resolve when Application.Current is null in Revit's net48 host).
    /// </summary>
    private void InitializeVersionGridHeaders()
    {
        ColVersionLabel.Header = LanguageManager.GetString(StringLocalization.Keys.FM_Version_Column_Version) ?? "Version";
        ColVersionRevit.Header = LanguageManager.GetString(StringLocalization.Keys.FM_Version_Column_Revit) ?? "Revit";
        ColVersionDate.Header = LanguageManager.GetString(StringLocalization.Keys.FM_Version_Column_Date) ?? "Date";
        ColVersionTypes.Header = LanguageManager.GetString(StringLocalization.Keys.FM_Version_Column_Types) ?? "Types";
        ColVersionActive.Header = LanguageManager.GetString(StringLocalization.Keys.FM_Version_Column_Active) ?? "Status";
    }
}
