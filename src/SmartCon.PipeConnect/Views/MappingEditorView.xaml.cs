using System.Windows.Controls;
using SmartCon.PipeConnect.Services;
using SmartCon.PipeConnect.ViewModels;
using SmartCon.UI;
using SmartCon.UI.Controls;

namespace SmartCon.PipeConnect.Views;

public partial class MappingEditorView : DialogWindowBase
{
    public MappingEditorView(MappingEditorViewModel viewModel)
    {
        InitializeComponent();
        LanguageManager.EnsureWindowResources(this);
        DataContext = viewModel;
        BindCloseRequest(viewModel);

        ApplyColumnHeaders();
    }

    // I-12: UI-only — commit pending edit on tab change
    private void TabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, sender))
            TypesGrid.CommitEdit(DataGridEditingUnit.Row, true);
    }

    private void ApplyColumnHeaders()
    {
        ColCode.Header = LanguageManager.GetString(StringLocalization.Keys.Col_Code);
        ColName.Header = LanguageManager.GetString(StringLocalization.Keys.Col_Name);
        ColDescription.Header = LanguageManager.GetString(StringLocalization.Keys.Col_Description);

        ColConnType1.Header = LanguageManager.GetString(StringLocalization.Keys.Col_ConnType1);
        ColConnType2.Header = LanguageManager.GetString(StringLocalization.Keys.Col_ConnType2);
        ColDirect.Header = LanguageManager.GetString(StringLocalization.Keys.Col_Direct);
        ColFittings.Header = LanguageManager.GetString(StringLocalization.Keys.Col_Fittings);
        ColTransitions.Header = LanguageManager.GetString(StringLocalization.Keys.Col_Transitions);
    }
}
