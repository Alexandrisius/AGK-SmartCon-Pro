using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyBatchImportRow
{
    /// <summary>
    /// VM-owned selection state. Bound to <c>DataGridRow.IsSelected</c> in
    /// XAML so that the selection survives clicks on inline editors
    /// (ComboBox dropdown, "…" Button) — those clicks collapse
    /// <c>DataGrid.SelectedItems</c> but the row stays visually selected
    /// because <c>IsSelected</c> is driven by this property.
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    [NotifyPropertyChangedFor(nameof(AvailableActions))]
    private FamilyBatchImportAction _action;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableActions))]
    [NotifyPropertyChangedFor(nameof(TypeNames))]
    [NotifyPropertyChangedFor(nameof(HasTypeNames))]
    private string _fileName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCategoryMoveWarning))]
    [NotifyPropertyChangedFor(nameof(CategoryMoveWarningTooltip))]
    [NotifyPropertyChangedFor(nameof(IsOutdatedNested))]
    [NotifyPropertyChangedFor(nameof(OutdatedNestedTooltip))]
    [NotifyPropertyChangedFor(nameof(ShowDiffBadge))]
    private FamilyBatchImportStatus _status;

    [ObservableProperty]
    private string? _existingCatalogItemId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOutdatedNested))]
    [NotifyPropertyChangedFor(nameof(OutdatedNestedTooltip))]
    private string? _existingVersionLabel;

    public bool CanImport => Action != FamilyBatchImportAction.Skip;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GateTooltip))]
    private FamilyBatchImportRowState _importRowState = FamilyBatchImportRowState.Pending;

    [ObservableProperty]
    private string? _importErrorMessage;

    partial void OnActionChanged(FamilyBatchImportAction value)
    {
        if (_suppressActionBroadcast) return;
        ActionChanged?.Invoke(this, value);
    }

    partial void OnStatusChanged(FamilyBatchImportStatus value)
    {
        RebuildNotices();

        // Gate block wins over the status-driven action set: a blocked row
        // stays forced to Skip no matter how the dedup status flips.
        // E2 (#209): the outdated-nested import block wins too — a rename
        // of a blocked parent must not restore the full action set.
        if (IsGateBlocked || HasOutdatedDependencyBlock)
        {
            AvailableActions = [FamilyBatchImportAction.Skip];
            SetActionSilently(FamilyBatchImportAction.Skip);
            return;
        }

        // v2.0.1 hotfix: recompute AvailableActions when Status flips so
        // OverwriteCurrent appears for Existing and disappears for New.
        // Previously the list was built once in the constructor, so a
        // rename Existing → New kept OverwriteCurrent (or vice versa).
        AvailableActions = BuildAvailableActions(value);

        // Phase 27: Duplicate defaults to Skip (no point creating a new
        // version with identical content). User can manually switch to
        // IncrementVersion if they want to force a new version.
        if (value == FamilyBatchImportStatus.Duplicate)
        {
            Action = FamilyBatchImportAction.Skip;
            return;
        }

        // Validate current Action against the new available set; if the
        // user previously selected OverwriteCurrent and the row became
        // New (or the row became Existing but Action was set during New
        // phase), reset to IncrementVersion. This keeps the combo box
        // bound to Action from ever holding an invalid value.
        if (!AvailableActions.Contains(Action))
        {
            Action = AvailableActions.Contains(FamilyBatchImportAction.IncrementVersion)
                ? FamilyBatchImportAction.IncrementVersion
                : FamilyBatchImportAction.Skip;
        }
    }

    partial void OnFileNameChanged(string value)
    {
        // v2.0.0 hotfix: notify the parent view-model so it can re-resolve
        // the catalog status (New/Existing) when the user renames the row.
        // Without this, the Status column would stay "Existing" even after
        // the user typed a unique name, leaving the dialog visually
        // inconsistent with what would actually happen on import.
        NameChanged?.Invoke(this, value);
    }

    partial void OnTargetCategoryPathChanged(string value)
    {
        // Fire only on path change so we always have a consistent (Id, Path)
        // pair. Picker flow sets Id first, then Path, so this fires after
        // both values are in place.
        CategoryChanged?.Invoke(this, (TargetCategoryId, value));
    }

    partial void OnIsSelectedChanged(bool value)
    {
        SelectionChanged?.Invoke(this, value);
    }

    partial void OnGateStatusChanged(FamilyRowGateStatus value)
    {
        if (value == FamilyRowGateStatus.Failed)
        {
            AvailableActions = [FamilyBatchImportAction.Skip];
            SetActionSilently(FamilyBatchImportAction.Skip);
            return;
        }

        // E2 (#209): a dependency-blocked row stays forced to Skip even when
        // the gate unblocks — the two block sources are independent.
        if (HasOutdatedDependencyBlock)
        {
            AvailableActions = [FamilyBatchImportAction.Skip];
            SetActionSilently(FamilyBatchImportAction.Skip);
            return;
        }

        // Unblock: restore the status-driven action set and reset the
        // forced Skip so a row that passes after a category change
        // becomes importable again (the user can re-pick Skip manually).
        var wasBlocked = !AvailableActions.Contains(FamilyBatchImportAction.IncrementVersion)
            && Action == FamilyBatchImportAction.Skip;
        AvailableActions = BuildAvailableActions(Status);
        if (value != FamilyRowGateStatus.Checking
            && (wasBlocked || !AvailableActions.Contains(Action)))
        {
            SetActionSilently(
                AvailableActions.Contains(FamilyBatchImportAction.IncrementVersion)
                    ? FamilyBatchImportAction.IncrementVersion
                    : FamilyBatchImportAction.Skip);
        }
    }

    /// <summary>
    /// E2 (#209): the outdated-nested import block forces Skip exactly like
    /// the validation gate does; clearing the block restores the
    /// status-driven action set (mirrors <see cref="OnGateStatusChanged"/>).
    /// Block-driven changes never broadcast to the multi-selection.
    /// </summary>
    partial void OnOutdatedDependencyBlockNamesChanged(IReadOnlyList<string>? value)
    {
        RebuildNotices();

        if (HasOutdatedDependencyBlock)
        {
            AvailableActions = [FamilyBatchImportAction.Skip];
            SetActionSilently(FamilyBatchImportAction.Skip);
            return;
        }

        if (IsGateBlocked) return;

        var wasBlocked = !AvailableActions.Contains(FamilyBatchImportAction.IncrementVersion)
            && Action == FamilyBatchImportAction.Skip;
        AvailableActions = BuildAvailableActions(Status);
        if (wasBlocked || !AvailableActions.Contains(Action))
        {
            SetActionSilently(
                AvailableActions.Contains(FamilyBatchImportAction.IncrementVersion)
                    ? FamilyBatchImportAction.IncrementVersion
                    : FamilyBatchImportAction.Skip);
        }
    }

    /// <summary>
    /// Gate-driven action changes must NOT batch-propagate to the other
    /// selected rows: a forced Skip (or its reset) is this row's own
    /// verdict, not a user instruction for the whole selection. User
    /// picks in the combo box keep broadcasting via
    /// <see cref="ActionChanged"/>.
    /// </summary>
    private bool _suppressActionBroadcast;

    private void SetActionSilently(FamilyBatchImportAction action)
    {
        _suppressActionBroadcast = true;
        try
        {
            Action = action;
        }
        finally
        {
            _suppressActionBroadcast = false;
        }
    }
}
