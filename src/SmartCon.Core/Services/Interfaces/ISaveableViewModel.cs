namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Enterprise pattern: saveable dialog ViewModel.
/// Implement this on any dialog ViewModel that performs data modifications
/// and should prompt "Save / Discard / Cancel" when the user closes the window.
/// </summary>
/// <remarks>
/// This is the canonical pattern for SmartCon dialog close confirmation.
/// See DialogCloseHelper.ConfirmUnsavedChanges() for the reusable confirmation logic.
/// See DialogWindowBase for the infrastructure that invokes this pattern.
/// </remarks>
public interface ISaveableViewModel
{
    /// <summary>
    /// Returns true when the ViewModel has unsaved modifications.
    /// </summary>
    bool HasUnsavedChanges { get; }

    /// <summary>
    /// Persists all pending changes. Called asynchronously by DialogWindowBase
    /// when the user chooses "Save" in the close confirmation dialog.
    /// Upon success the ViewModel must call RequestClose(true) to close the window.
    /// </summary>
    Task SaveAsync();
}
