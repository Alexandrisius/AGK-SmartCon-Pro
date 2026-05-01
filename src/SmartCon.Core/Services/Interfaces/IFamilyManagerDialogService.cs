using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

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

    /// <summary>Show metadata edit dialog for a catalog item.</summary>
    bool? ShowMetadataEdit(object viewModel);

    /// <summary>Show simple input dialog and return entered text, or null if cancelled.</summary>
    string? ShowInputDialog(string title, string prompt, string defaultText = "");

    /// <summary>Show Yes/No confirmation dialog. Returns true if user clicked Yes.</summary>
    bool ShowConfirmation(string title, string message);

    /// <summary>Show category tree editor dialog.</summary>
    bool? ShowCategoryTreeEditor(object viewModel);

    /// <summary>Show category picker dialog and return selected category ID, or null if cancelled.</summary>
    string? ShowCategoryPicker(object viewModel);

    /// <summary>Show open file dialog filtered for .json files.</summary>
    string? ShowOpenJsonDialog(string title, string? initialDirectory = null);

    /// <summary>Show save file dialog for .json files.</summary>
    string? ShowSaveJsonDialog(string title, string? defaultFileName = null);
}
