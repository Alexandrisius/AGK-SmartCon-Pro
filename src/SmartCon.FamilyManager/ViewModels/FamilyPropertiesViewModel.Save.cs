using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Helpers;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyPropertiesViewModel
{
    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task PickCategory()
    {
        var pickerVm = _viewModelFactory.CreateCategoryPickerViewModel();
        await pickerVm.InitializeAsync();
        var result = _dialogService.ShowCategoryPicker(pickerVm);
        if (result is not null)
        {
            if (string.IsNullOrEmpty(result))
            {
                CategoryId = null;
                CategoryPath = LanguageManager.GetString(StringLocalization.Keys.FM_NoCategory) ?? "No category";
            }
            else if (string.Equals(CategoryId, result, StringComparison.Ordinal))
            {
                // Same category re-picked — nothing to change, no gate.
            }
            else
            {
                // Import Validation Gate: a rule-protected category accepts
                // the family only when it passes the rules (persisted
                // extraction — no .rfa re-open). Blocked = dialog shown,
                // selection aborted.
                if (!await _categoryChangeGate.EnsureFamilyPassesAsync(
                        _catalogItemId, Name, result, pickerVm.SelectedPath))
                {
                    return;
                }

                CategoryId = result;
                CategoryPath = pickerVm.SelectedPath;
            }
        }
    }

    public async Task SaveAsync()
    {
        if (IsReadOnly) return;
        if (!await _updateState.EnsureUpToDateAsync().ConfigureAwait(true)) return;
        try
        {
            // ADR-072 Phase 3 (World B): routing edits save first (in-place
            // item-level link update — no catalog version is created); a
            // routing failure aborts the whole save.
            if (HasRoutingChanges && !await SaveRoutingAsync().ConfigureAwait(true))
                return;

            SmartConLogger.Info($"Saving for {_catalogItemId}, new name='{Name}'");

            var tags = Tags.ToList();

            await _writableProvider.UpdateItemAsync(
                _catalogItemId,
                Name,
                Description,
                CategoryId,
                tags,
                ContentStatus);

            SmartConLogger.Info($"DB updated, renaming files...");
            await _renameService.RenameFamilyFilesAsync(_catalogItemId, Name);
            SmartConLogger.Info($"Rename completed");

            RequestClose?.Invoke(true);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"FAILED: {ex.Message}\n{ex.StackTrace}");
            _dialogService.ShowError("Family Manager", $"Failed to save: {ex.Message}");
        }
    }

    [RelayCommand(CanExecute = nameof(CanWrite))]
    private async Task Ok() => await SaveAsync();

    public void ConfirmClose(CloseConfirmationArgs args) =>
        this.ConfirmUnsavedChanges(
            args,
            _dialogService.ShowYesNoCancel,
            LanguageManager.GetString(StringLocalization.Keys.FM_CTE_UnsavedChangesTitle) ?? "Unsaved Changes",
            LanguageManager.GetString(StringLocalization.Keys.FM_CTE_UnsavedChangesMessage) ?? "You have unsaved changes. Save before closing?");

    [RelayCommand]
    private async Task CancelAsync()
    {
        if (HasUnsavedChanges)
        {
            var result = _dialogService.ShowYesNoCancel(
                LanguageManager.GetString(StringLocalization.Keys.FM_CTE_UnsavedChangesTitle) ?? "Unsaved Changes",
                LanguageManager.GetString(StringLocalization.Keys.FM_CTE_UnsavedChangesMessage) ?? "You have unsaved changes. Save before closing?");

            if (result == Core.Services.Interfaces.DialogResult.Yes)
            {
                await SaveAsync();
                return;
            }

            if (result == Core.Services.Interfaces.DialogResult.Cancel)
                return;
        }

        RequestClose?.Invoke(null);
    }

    private bool CanWrite() => !IsReadOnly;

    public bool CanWriteProperty => !IsReadOnly;
}
