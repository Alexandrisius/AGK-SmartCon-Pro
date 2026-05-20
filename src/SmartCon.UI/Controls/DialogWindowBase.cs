using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // Don't intercept Escape when editing a DataGrid cell
            if (e.OriginalSource is DataGridCell ||
                (e.OriginalSource is FrameworkElement fe && fe.Parent is DataGridCell))
            {
                return;
            }

            // Find the cancel button and invoke its command or click
            var cancelButton = FindCancelButton(this);
            if (cancelButton is not null && cancelButton.IsVisible && cancelButton.IsEnabled)
            {
                if (cancelButton.Command?.CanExecute(cancelButton.CommandParameter) == true)
                {
                    cancelButton.Command.Execute(cancelButton.CommandParameter);
                }
                else
                {
                    cancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
            }
            else
            {
                Close();
            }
            e.Handled = true;
        }
    }

    private static Button? FindCancelButton(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Button button && button.IsCancel)
                return button;
            var result = FindCancelButton(child);
            if (result is not null)
                return result;
        }
        return null;
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
        try { DialogResult = result; } catch (InvalidOperationException) { }
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
