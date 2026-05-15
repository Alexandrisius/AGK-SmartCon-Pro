using SmartCon.UI;
using SmartCon.UI.Controls;
using SmartCon.ProjectManagement.ViewModels;

namespace SmartCon.ProjectManagement.Views;

public partial class ParseRuleView : DialogWindowBase
{
    public ParseRuleView(ParseRuleViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        BindCloseRequest(viewModel);
    }
}
