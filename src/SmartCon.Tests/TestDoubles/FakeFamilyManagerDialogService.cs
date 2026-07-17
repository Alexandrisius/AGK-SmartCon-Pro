using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Tests.TestDoubles;

/// <summary>
/// Minimal <see cref="IFamilyManagerDialogService"/> fake: only
/// <see cref="ShowConfirmation"/> is implemented (configurable answer +
/// call counter). Everything else throws <see cref="NotImplementedException"/>
/// so accidental usage in a test fails loudly.
/// </summary>
public sealed class FakeFamilyManagerDialogService : IFamilyManagerDialogService
{
    public bool ConfirmationAnswer { get; set; } = true;
    public int ConfirmationCalls { get; private set; }
    public string? LastConfirmationTitle { get; private set; }
    public string? LastConfirmationMessage { get; private set; }

    public bool ShowConfirmation(string title, string message)
    {
        ConfirmationCalls++;
        LastConfirmationTitle = title;
        LastConfirmationMessage = message;
        return ConfirmationAnswer;
    }

    public string? ShowOpenFileDialog(string title, string? initialDirectory = null) => throw new NotImplementedException();
    public string? ShowImportDialog(string title, string? initialDirectory = null) => throw new NotImplementedException();
    public string[]? ShowImportFilesDialog(string title, string? initialDirectory = null) => throw new NotImplementedException();
    public string? ShowFolderBrowserDialog(string title, string? initialDirectory = null) => throw new NotImplementedException();
    public void ShowWarning(string title, string message) => throw new NotImplementedException();
    public void ShowError(string title, string message) => throw new NotImplementedException();
    public string? ShowInputDialog(string title, string prompt, string defaultText = "") => throw new NotImplementedException();
    public DialogResult ShowYesNoCancel(string title, string message) => throw new NotImplementedException();
    public bool? ShowCategoryTreeEditor(object viewModel) => throw new NotImplementedException();
    public bool? ShowProjectBaseRulesEditor(object viewModel) => throw new NotImplementedException();
    public bool? ShowParseRuleEditor(object viewModel) => throw new NotImplementedException();
    public bool? ShowFieldLibrary(object viewModel) => throw new NotImplementedException();
    public bool? ShowAllowedValues(object viewModel) => throw new NotImplementedException();
    public string? ShowCategoryPicker(object viewModel) => throw new NotImplementedException();
    public string? ShowOpenJsonDialog(string title, string? initialDirectory = null) => throw new NotImplementedException();
    public string? ShowSaveJsonDialog(string title, string? defaultFileName = null) => throw new NotImplementedException();
    public bool? ShowProperties(object viewModel) => throw new NotImplementedException();
    public string? ShowAssetOpenFileDialog(string title, FamilyAssetType assetType, string? initialDirectory = null) => throw new NotImplementedException();
    public bool? ShowPresetEditor(object viewModel) => throw new NotImplementedException();
    public bool? ShowAttributeLibrary(object viewModel) => throw new NotImplementedException();
    public bool? ShowProfile(object viewModel) => throw new NotImplementedException();
    public bool? ShowBatchImportDialog(object viewModel) => throw new NotImplementedException();
    public void ShowModelessBatchImportDialog(object viewModel) => throw new NotImplementedException();
    public void ShowHashRecalculationProgressDialog(object viewModel) => throw new NotImplementedException();
    public bool? ShowAvatarCropper(object viewModel) => throw new NotImplementedException();
    public SharedFamiliesLoadChoice ShowSharedFamiliesLoadModeDialog(SharedFamilyDecisionRequest request) => throw new NotImplementedException();
}
