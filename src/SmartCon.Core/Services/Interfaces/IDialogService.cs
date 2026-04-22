using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Abstraction for showing modal dialogs from ViewModels.
/// Implementations live in the UI layer (SmartCon.PipeConnect).
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Shows a mini-dialog to pick a single connector type from the available list.
    /// Returns the selected type or <c>null</c> on cancel.
    /// </summary>
    ConnectorTypeDefinition? ShowMiniTypeSelector(
        IReadOnlyList<ConnectorTypeDefinition> availableTypes);

    /// <summary>Shows a warning dialog with the specified title and message.</summary>
    void ShowWarning(string title, string message);

    /// <summary>
    /// Shows a dialog to select fitting families for mapping rules.
    /// Returns updated selection or <c>null</c> on cancel.
    /// </summary>
    IReadOnlyList<FittingMapping>? ShowFamilySelector(
        IReadOnlyList<string> availableFamilies,
        IReadOnlyList<FittingMapping> currentSelection);

    /// <summary>
    /// Shows a dialog to assign connector types (CTC) to fitting connectors.
    /// Returns <c>true</c> if the user confirmed assignments.
    /// </summary>
    [Obsolete("LEGACY: CTC now assigned automatically via Reflect button. No callers.")]
    bool ShowFittingCtcSetup(
        string familyName,
        string symbolName,
        IReadOnlyList<IFittingCtcSetupItem> connectors,
        IReadOnlyList<ConnectorTypeDefinition> availableTypes);

    /// <summary>
    /// Shows an Open File dialog filtered to JSON files and returns the selected path,
    /// or <c>null</c> when the user cancels.
    /// </summary>
    /// <param name="title">Dialog window title.</param>
    /// <param name="initialDirectory">
    /// Optional initial directory. When the folder exists the dialog opens there; otherwise
    /// the system default is used (typically <c>Documents</c>).
    /// </param>
    /// <param name="preselectFileName">
    /// Optional file name to preselect in the dialog (only used together with a valid
    /// <paramref name="initialDirectory"/>).
    /// </param>
    string? ShowOpenJsonDialog(string title, string? initialDirectory = null, string? preselectFileName = null);

    /// <summary>
    /// Shows a Save File dialog filtered to JSON files and returns the target path,
    /// or <c>null</c> when the user cancels.
    /// </summary>
    /// <param name="title">Dialog window title.</param>
    /// <param name="defaultFileName">Optional default file name suggested by the dialog.</param>
    string? ShowSaveJsonDialog(string title, string? defaultFileName = null);
}
