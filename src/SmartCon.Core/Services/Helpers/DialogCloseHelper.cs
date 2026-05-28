using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Core.Services.Helpers;

/// <summary>
/// Enterprise pattern: reusable dialog close confirmation helper.
/// Attach this to any ISaveableViewModel.ConfirmClose() implementation.
/// </summary>
/// <remarks>
/// This is the canonical SmartCon pattern for "unsaved changes" dialogs.
/// Usage in a ViewModel:
///   public void ConfirmClose(CloseConfirmationArgs args) =>
///       this.ConfirmUnsavedChanges(args, _dialogService.ShowYesNoCancel);
/// 
/// The helper shows a Yes/No/Cancel dialog and wires up AsyncDeferredAction
/// so DialogWindowBase can await SaveAsync() before closing.
/// </remarks>
public static class DialogCloseHelper
{
    /// <summary>
    /// Checks <see cref="ISaveableViewModel.HasUnsavedChanges"/> and, when dirty,
    /// shows a Yes/No/Cancel confirmation dialog. Sets <paramref name="args"/>
    /// appropriately so DialogWindowBase can cancel, save, or discard.
    /// </summary>
    /// <param name="vm">The saveable ViewModel.</param>
    /// <param name="args">Close confirmation arguments to mutate.</param>
    /// <param name="showYesNoCancel">
    /// Function that shows a Yes/No/Cancel dialog and returns the user's choice.
    /// Signature: (title, message) => DialogResult.
    /// </param>
    public static void ConfirmUnsavedChanges(
        this ISaveableViewModel vm,
        CloseConfirmationArgs args,
        Func<string, string, DialogResult> showYesNoCancel)
    {
        ConfirmUnsavedChanges(vm, args, showYesNoCancel,
            "Unsaved Changes",
            "You have unsaved changes. Save before closing?");
    }

    /// <summary>
    /// Checks <see cref="ISaveableViewModel.HasUnsavedChanges"/> and, when dirty,
    /// shows a Yes/No/Cancel confirmation dialog with the supplied title and message.
    /// Sets <paramref name="args"/> appropriately so DialogWindowBase can cancel,
    /// save, or discard.
    /// </summary>
    /// <param name="vm">The saveable ViewModel.</param>
    /// <param name="args">Close confirmation arguments to mutate.</param>
    /// <param name="showYesNoCancel">
    /// Function that shows a Yes/No/Cancel dialog and returns the user's choice.
    /// Signature: (title, message) => DialogResult.
    /// </param>
    /// <param name="title">Dialog title (should be localized by caller).</param>
    /// <param name="message">Dialog message (should be localized by caller).</param>
    public static void ConfirmUnsavedChanges(
        this ISaveableViewModel vm,
        CloseConfirmationArgs args,
        Func<string, string, DialogResult> showYesNoCancel,
        string title,
        string message)
    {
        if (!vm.HasUnsavedChanges)
            return;

        var result = showYesNoCancel(title, message);

        switch (result)
        {
            case DialogResult.Yes:
                args.Cancel = true;
                args.AsyncDeferredAction = vm.SaveAsync;
                break;

            case DialogResult.No:
                args.DialogResult = false;
                break;

            case DialogResult.Cancel:
            default:
                args.Cancel = true;
                break;
        }
    }
}
