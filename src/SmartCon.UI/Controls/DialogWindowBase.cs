using System.ComponentModel;
using System.Windows;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.UI.Controls;

public class DialogWindowBase : Window
{
    private bool _closeFromViewModel;
    private ICloseAwareViewModel? _closeAwareViewModel;

    public bool? CustomDialogResult { get; protected set; }

    public DialogWindowBase()
    {
        Topmost = true;
    }

    protected void BindCloseRequest(IObservableRequestClose viewModel)
    {
        viewModel.RequestClose += OnViewModelRequestClose;
        Closing += HandleClosing;

        if (viewModel is ICloseAwareViewModel aware)
            _closeAwareViewModel = aware;
    }

    private void OnViewModelRequestClose(bool? result)
    {
        CustomDialogResult = result;
        try { DialogResult = result; } catch (InvalidOperationException) { }
        _closeFromViewModel = true;
        Close();
        _closeFromViewModel = false;
    }

    private void HandleClosing(object? sender, CancelEventArgs e)
    {
        if (_closeFromViewModel) return;

        if (_closeAwareViewModel is not null)
        {
            var args = new CloseConfirmationArgs();
            _closeAwareViewModel.ConfirmClose(args);

            if (args.Cancel)
            {
                e.Cancel = true;
                if (args.DeferredAction is not null)
                    Dispatcher.BeginInvoke(args.DeferredAction);
                return;
            }

            CustomDialogResult = args.DialogResult ?? false;
        }
        else
        {
            CustomDialogResult = false;
        }
    }
}
