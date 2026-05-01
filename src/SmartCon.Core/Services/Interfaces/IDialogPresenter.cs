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
}
