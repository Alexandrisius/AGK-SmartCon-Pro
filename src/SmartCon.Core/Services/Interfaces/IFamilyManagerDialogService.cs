using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public enum DialogResult
{
    None,
    OK,
    Cancel,
    Yes,
    No
}

/// <summary>
/// UI dialogs for FamilyManager module.
/// </summary>
public interface IFamilyManagerDialogService
{
    /// <summary>Show file open dialog filtered for .rfa files.</summary>
    string? ShowOpenFileDialog(string title, string? initialDirectory = null);

    /// <summary>Show import dialog: select a file (.rfa) or navigate to a folder to import all families.</summary>
    string? ShowImportDialog(string title, string? initialDirectory = null);

    /// <summary>Show multi-file import dialog: select multiple .rfa files.</summary>
    string[]? ShowImportFilesDialog(string title, string? initialDirectory = null);

    /// <summary>Show folder browser dialog.</summary>
    string? ShowFolderBrowserDialog(string title, string? initialDirectory = null);

    /// <summary>Show warning message.</summary>
    void ShowWarning(string title, string message);

    /// <summary>Show error message.</summary>
    void ShowError(string title, string message);

    /// <summary>
    /// Show an informational message in the styled dialog with a single
    /// "Понятно" button (the OK-only variant of the confirmation dialog).
    /// Prefer this over <see cref="ShowWarning"/> for gate/explanation
    /// dialogs in FamilyManager — the system MessageBox looks foreign.
    /// </summary>
    void ShowInfo(string title, string message);

    /// <summary>Show simple input dialog and return entered text, or null if cancelled.</summary>
    string? ShowInputDialog(string title, string prompt, string defaultText = "");

    /// <summary>Show Yes/No confirmation dialog. Returns true if user clicked Yes.</summary>
    bool ShowConfirmation(string title, string message);

    DialogResult ShowYesNoCancel(string title, string message);

    /// <summary>Show category tree editor dialog.</summary>
    bool? ShowCategoryTreeEditor(object viewModel);

    /// <summary>
    /// Show the project-base rules editor dialog (see #119). Returns
    /// <c>true</c> if the user clicked OK and the binding is valid,
    /// <c>false</c> if the user cancelled or closed the dialog, or
    /// <c>null</c> if the dialog presenter returned an unexpected value.
    /// </summary>
    bool? ShowProjectBaseRulesEditor(object viewModel);

    /// <summary>Show the parse-rule editor sub-dialog.</summary>
    bool? ShowParseRuleEditor(object viewModel);

    /// <summary>Show the field-library editor sub-dialog.</summary>
    bool? ShowFieldLibrary(object viewModel);

    /// <summary>Show the allowed-values editor sub-dialog.</summary>
    bool? ShowAllowedValues(object viewModel);

    /// <summary>Show category picker dialog and return selected category ID, or null if cancelled.</summary>
    string? ShowCategoryPicker(object viewModel);

    /// <summary>Show open file dialog filtered for .json files.</summary>
    string? ShowOpenJsonDialog(string title, string? initialDirectory = null);

    /// <summary>Show open file dialog filtered for .txt files (shared parameters file).</summary>
    string? ShowOpenTextFileDialog(string title, string? initialDirectory = null);

    /// <summary>
    /// Show the shared-parameter picker dialog (import attributes from a ФОП
    /// file). Returns true when the user confirmed the selection — read the
    /// selected entries from the view model afterwards.
    /// </summary>
    bool? ShowSharedParameterPicker(object viewModel);

    /// <summary>Show save file dialog for .json files.</summary>
    string? ShowSaveJsonDialog(string title, string? defaultFileName = null);

    bool? ShowProperties(object viewModel);

    string? ShowAssetOpenFileDialog(string title, FamilyAssetType assetType, string? initialDirectory = null);

    bool? ShowPresetEditor(object viewModel);

    bool? ShowAttributeLibrary(object viewModel);

    bool? ShowProfile(object viewModel);

    /// <summary>Show batch import dialog with file list and action selection.</summary>
    bool? ShowBatchImportDialog(object viewModel);

    /// <summary>
    /// Show the validation report dialog (import validation gate): the
    /// read-only detail of why a batch row passed/failed — health issues
    /// and rule violations. Returns when the user closes it.
    /// </summary>
    bool? ShowValidationReport(object viewModel);

    /// <summary>
    /// Show the validation rules editor for one category-attribute
    /// binding. Returns true when the user saved the rules.
    /// </summary>
    bool? ShowValidationRulesEditor(object viewModel);

    /// <summary>
    /// Show the batch import dialog as a modeless window (Issue #127): the
    /// dialog stays open during the import and drives progress/cancellation
    /// through its view model. Returns immediately; the caller awaits the
    /// view model's completion task.
    /// </summary>
    void ShowModelessBatchImportDialog(object viewModel);

    /// <summary>
    /// Show the unified database update dialog (ADR-054) as a modeless
    /// window (ADR-048 pattern): ONE dialog runs the actualization engine
    /// over every pending family. Returns immediately; the caller runs the
    /// update via the view model and awaits its completion.
    /// </summary>
    void ShowDatabaseUpdateProgressDialog(object viewModel);

    /// <summary>
    /// Show the avatar crop dialog (issue #131, ADR-047). The viewModel must be a
    /// CropAvatarViewModel; returns true when the user applied the crop — read
    /// ResultPath from the viewModel in that case.
    /// </summary>
    bool? ShowAvatarCropper(object viewModel);

    /// <summary>
    /// Show dialog asking the user how to load a single shared nested family
    /// that conflicts with an existing one in the project.
    /// MUST be called from Revit main thread (blocks until user decides).
    /// The dialog has no Cancel button and no Escape binding — the user must
    /// make an explicit choice. Closing the dialog via the window X button
    /// (or Alt+F4) returns <see cref="SharedFamiliesLoadChoice.Skip"/>.
    /// </summary>
    /// <param name="request">Information about the conflicting family.</param>
    /// <returns>
    /// User choice. If the user closes the dialog without selecting Apply,
    /// returns <see cref="SharedFamiliesLoadChoice.Skip"/> — the callback
    /// signals Revit to abort loading this shared nested family.
    /// </returns>
    SharedFamiliesLoadChoice ShowSharedFamiliesLoadModeDialog(SharedFamilyDecisionRequest request);
}
