namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Presents modal dialogs for a view model by resolving the corresponding WPF view.
/// </summary>
public interface IDialogPresenter
{
    /// <summary>
    /// Shows the dialog associated with the specified view model.
    /// </summary>
    /// <typeparam name="TViewModel">Type of the view model.</typeparam>
    /// <param name="viewModel">View model instance bound to the dialog.</param>
    /// <returns>Dialog result returned by the underlying window.</returns>
    bool? ShowDialog<TViewModel>(TViewModel viewModel) where TViewModel : class;

    /// <summary>
    /// Shows the dialog associated with the specified view model (non-generic).
    /// </summary>
    /// <param name="viewModel">View model instance bound to the dialog.</param>
    /// <returns>Dialog result returned by the underlying window.</returns>
    bool? ShowDialog(object viewModel);

    /// <summary>
    /// Shows the view associated with the specified view model as a modeless
    /// window (Window.Show semantics) and returns immediately. The caller
    /// tracks the dialog lifecycle through the view model (e.g. an
    /// IObservableRequestClose completion task).
    /// </summary>
    /// <param name="viewModel">View model instance bound to the view.</param>
    void ShowModeless(object viewModel);
}
