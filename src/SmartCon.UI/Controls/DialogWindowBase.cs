using System.ComponentModel;
using System.Windows;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.UI.Controls;

public class DialogWindowBase : Window
{
    private bool _closeFromViewModel;

    public bool? CustomDialogResult { get; protected set; }

    protected void BindCloseRequest(IObservableRequestClose viewModel)
    {
        viewModel.RequestClose += OnViewModelRequestClose;
        Closing += HandleClosing;
    }

    private void OnViewModelRequestClose(bool? result)
    {
        CustomDialogResult = result;
        _closeFromViewModel = true;
        Close();
        _closeFromViewModel = false;
    }

    private void HandleClosing(object? sender, CancelEventArgs e)
    {
        if (_closeFromViewModel) return;
        OnUserInitiatedClose(e);
    }

    protected virtual void OnUserInitiatedClose(CancelEventArgs e)
    {
    }
}
