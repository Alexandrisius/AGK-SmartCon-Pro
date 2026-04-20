using System.ComponentModel;
using System.Windows;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.UI.Controls;

/// <summary>
/// Base class for WPF dialog windows bound to a ViewModel implementing
/// <see cref="IObservableRequestClose"/>. Centralises the common
/// "close-from-VM vs close-from-user" interception pattern.
/// </summary>
/// <remarks>
/// Default behaviour: user-initiated close (e.g. close button, Alt+F4) is
/// allowed. Derived views can override <see cref="OnUserInitiatedClose"/>
/// to route the close through the ViewModel (e.g. invoke a CancelCommand
/// and set <see cref="CancelEventArgs.Cancel"/> to <see langword="true"/>).
/// </remarks>
public class DialogWindowBase : Window
{
    private bool _closeFromViewModel;

    /// <summary>
    /// Subscribe to <see cref="IObservableRequestClose.RequestClose"/> and
    /// install a <see cref="Window.Closing"/> handler that distinguishes
    /// between VM-initiated close and user-initiated close.
    /// </summary>
    protected void BindCloseRequest(IObservableRequestClose viewModel)
    {
        viewModel.RequestClose += OnViewModelRequestClose;
        Closing += HandleClosing;
    }

    private void OnViewModelRequestClose()
    {
        _closeFromViewModel = true;
        Close();
        _closeFromViewModel = false;
    }

    private void HandleClosing(object? sender, CancelEventArgs e)
    {
        if (_closeFromViewModel) return;
        OnUserInitiatedClose(e);
    }

    /// <summary>
    /// Called when the user closes the window directly (not via VM RequestClose).
    /// Default: allow close. Override to cancel and route through VM.
    /// </summary>
    protected virtual void OnUserInitiatedClose(CancelEventArgs e)
    {
    }
}
