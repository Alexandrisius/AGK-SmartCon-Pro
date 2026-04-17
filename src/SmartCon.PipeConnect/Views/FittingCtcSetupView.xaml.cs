using System.Windows;
using SmartCon.PipeConnect.Services;
using SmartCon.PipeConnect.ViewModels;

namespace SmartCon.PipeConnect.Views;

public partial class FittingCtcSetupView : Window
{
    private bool _closeFromViewModel;

    public FittingCtcSetupView(FittingCtcSetupViewModel viewModel)
    {
        InitializeComponent();
        LanguageManager.EnsureWindowResources(this);
        DataContext = viewModel;
        viewModel.RequestClose += OnRequestClose;
        Closing += OnClosing;
    }

    private void OnRequestClose()
    {
        _closeFromViewModel = true;
        Close();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeFromViewModel) return;

        if (DataContext is FittingCtcSetupViewModel vm)
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(() => vm.CancelCommand.Execute(null));
        }
    }
}
