using System.ComponentModel;
using SmartCon.UI;
using SmartCon.UI.Controls;
using SmartCon.ProjectManagement.ViewModels;

namespace SmartCon.ProjectManagement.Views;

public partial class ParseRuleView : DialogWindowBase
{
    public ParseRuleView(ParseRuleViewModel viewModel)
    {
        InitializeComponent();
        LanguageManager.EnsureWindowResources(this);
        DataContext = viewModel;

        BindCloseRequest(viewModel);
    }

    protected override void OnUserInitiatedClose(CancelEventArgs e)
    {
        CustomDialogResult = false;
    }
}
