using System.Windows;
using SmartCon.PipeConnect.Services;
using SmartCon.PipeConnect.ViewModels;

namespace SmartCon.PipeConnect.Views;

public partial class MappingEditorView : Window
{
    public MappingEditorView(MappingEditorViewModel viewModel)
    {
        InitializeComponent();
        LanguageManager.EnsureWindowResources(this);
        DataContext = viewModel;

        ApplyColumnHeaders();
    }

    private void ApplyColumnHeaders()
    {
        TypesGrid.Columns[0].Header = LanguageManager.GetString(StringLocalization.Keys.Col_Code);
        TypesGrid.Columns[1].Header = LanguageManager.GetString(StringLocalization.Keys.Col_Name);
        TypesGrid.Columns[2].Header = LanguageManager.GetString(StringLocalization.Keys.Col_Description);

        RulesGrid.Columns[0].Header = LanguageManager.GetString(StringLocalization.Keys.Col_ConnType1);
        RulesGrid.Columns[1].Header = LanguageManager.GetString(StringLocalization.Keys.Col_ConnType2);
        RulesGrid.Columns[2].Header = LanguageManager.GetString(StringLocalization.Keys.Col_Direct);
        RulesGrid.Columns[3].Header = LanguageManager.GetString(StringLocalization.Keys.Col_Fittings);
        RulesGrid.Columns[4].Header = LanguageManager.GetString(StringLocalization.Keys.Col_Transitions);
    }
}
