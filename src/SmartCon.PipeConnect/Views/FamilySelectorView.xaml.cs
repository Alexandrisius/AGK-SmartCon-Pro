using System.ComponentModel;
using SmartCon.PipeConnect.Services;
using SmartCon.PipeConnect.ViewModels;
using SmartCon.UI;
using SmartCon.UI.Controls;

namespace SmartCon.PipeConnect.Views;

public partial class FamilySelectorView : DialogWindowBase
{
    public FamilySelectorView(FamilySelectorViewModel viewModel)
    {
        InitializeComponent();
        LanguageManager.EnsureWindowResources(this);
        DataContext = viewModel;
        BindCloseRequest(viewModel);
    }

    protected override void OnUserInitiatedClose(CancelEventArgs e)
    {
        if (DataContext is FamilySelectorViewModel vm)
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(() => vm.CancelCommand.Execute(null));
        }
    }
}
