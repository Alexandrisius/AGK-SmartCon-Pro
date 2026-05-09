using System.ComponentModel;
using SmartCon.UI;
using SmartCon.UI.Controls;
using SmartCon.ProjectManagement.ViewModels;

namespace SmartCon.ProjectManagement.Views;

public partial class ExportNameDialog : DialogWindowBase
{
    public ExportNameDialog(ExportNameDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        BindCloseRequest(viewModel);
    }

    protected override void OnUserInitiatedClose(CancelEventArgs e)
    {
        CustomDialogResult = false;
    }
}
