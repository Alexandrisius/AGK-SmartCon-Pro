using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.UI.Controls;

public class DialogWindowBase : Window
{
    private bool _closeFromViewModel;
    private bool _forceClose;
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
        _closeFromViewModel = true;
        _forceClose = true;
        try
        {
            try { DialogResult = result; } catch (InvalidOperationException) { }
            Close();
        }
        finally
        {
            _closeFromViewModel = false;
            _forceClose = false;
        }
    }

    private void HandleClosing(object? sender, CancelEventArgs e)
    {
        if (_closeFromViewModel || _forceClose) return;

        if (_closeAwareViewModel is not null)
        {
            var args = new CloseConfirmationArgs();
            _closeAwareViewModel.ConfirmClose(args);

            if (args.Cancel)
            {
                e.Cancel = true;
                if (args.AsyncDeferredAction is not null)
                {
                    Dispatcher.BeginInvoke(async () =>
                    {
                        try
                        {
                            await args.AsyncDeferredAction();
                        }
                        finally
                        {
                            _forceClose = false;
                        }
                    });
                }
                else if (args.DeferredAction is not null)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        try
                        {
                            args.DeferredAction();
                        }
                        finally
                        {
                            _forceClose = false;
                        }
                    });
                }
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
