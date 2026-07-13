using System.Windows;
using SmartCon.FamilyManager.ViewModels.ProjectBase;
using SmartCon.UI.Controls;

namespace SmartCon.FamilyManager.Views;

public partial class ParseRuleView : DialogWindowBase
{
    public ParseRuleView(ParseRuleViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        BindCloseRequest(viewModel);
    }
}
