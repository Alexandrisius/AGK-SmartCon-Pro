using System.ComponentModel;
using SmartCon.PipeConnect.Services;
using SmartCon.PipeConnect.ViewModels;
using SmartCon.UI.Controls;

namespace SmartCon.PipeConnect.Views;

[Obsolete("LEGACY: CTC now assigned automatically via Reflect button. Dialog no longer invoked.")]
public partial class FittingCtcSetupView : DialogWindowBase
{
    public FittingCtcSetupView(FittingCtcSetupViewModel viewModel)
    {
        InitializeComponent();
        LanguageManager.EnsureWindowResources(this);
        DataContext = viewModel;
        BindCloseRequest(viewModel);
    }

    protected override void OnUserInitiatedClose(CancelEventArgs e)
    {
        if (DataContext is FittingCtcSetupViewModel vm)
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(() => vm.CancelCommand.Execute(null));
        }
    }
}
