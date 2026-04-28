using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// UI dialogs for FamilyManager module.
/// Analogous to IDialogService (PipeConnect-specific) but for FamilyManager.
/// </summary>
public interface IFamilyManagerDialogService
{
    /// <summary>Show file open dialog filtered for .rfa files.</summary>
    string? ShowOpenFileDialog(string title, string? initialDirectory = null);

    /// <summary>Show folder browser dialog.</summary>
    string? ShowFolderBrowserDialog(string title, string? initialDirectory = null);

    /// <summary>Show warning message.</summary>
    void ShowWarning(string title, string message);

    /// <summary>Show error message.</summary>
    void ShowError(string title, string message);

    /// <summary>Show metadata edit dialog for a catalog item.</summary>
    bool? ShowMetadataEdit(object viewModel);
}
