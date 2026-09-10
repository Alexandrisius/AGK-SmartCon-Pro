using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyBatchImportViewModel
{
    private void OnRowSelectionChanged(FamilyBatchImportRow row, bool isSelected)
    {
        if (isSelected)
        {
            _selectedRows.Add(row);
        }
        else
        {
            _selectedRows.Remove(row);
        }
    }

    private void ApplyActionToSelection(FamilyBatchImportRow source, FamilyBatchImportAction newValue)
    {
        if (_batchApplying) return;
        _batchApplying = true;
        try
        {
            foreach (var target in GetOtherSelectedRows(source))
            {
                if (target.AvailableActions.Contains(newValue))
                {
                    target.Action = newValue;
                }
                else
                {
                    SmartCon.Core.Logging.SmartConLogger.Debug(
                        $"BatchImport.Action: skip apply {newValue} to '{target.FileName}' — not in AvailableActions");
                }
            }
        }
        finally
        {
            _batchApplying = false;
        }
    }

    private void ApplyCategoryToSelection(FamilyBatchImportRow source, string? id, string path)
    {
        if (_batchApplying) return;
        _batchApplying = true;
        var applied = 0;
        var skippedLocked = 0;
        try
        {
            foreach (var target in GetOtherSelectedRows(source))
            {
                // Issue #135 defect 3: an AUTOMATIC change (rename
                // re-derivation) must not clobber a locked category on
                // other selected rows; an explicit user choice (picker,
                // provenance Manual) applies to everyone, locks included.
                if (target.TargetCategoryIsManual && !source.TargetCategoryIsManual)
                {
                    skippedLocked++;
                    continue;
                }
                target.CategoryProvenance = source.CategoryProvenance;
                target.ClearCategoryOnImport = source.ClearCategoryOnImport;
                target.TargetCategoryId = id;
                target.TargetCategoryPath = path;
                applied++;
            }
        }
        finally
        {
            _batchApplying = false;
        }
        if (applied > 0 || skippedLocked > 0)
        {
            SmartConLogger.Debug(
                $"BatchImport.Category: batch-applied '{path}' to {applied} row(s) from '{source.FileName}' " +
                $"(provenance={source.CategoryProvenance}, skippedLocked={skippedLocked})");
        }
    }

    private List<FamilyBatchImportRow> GetOtherSelectedRows(FamilyBatchImportRow source)
    {
        // Exclude the source so the setter isn't fired twice (it would
        // still be idempotent but would emit an extra PropertyChanged and
        // a redundant UpdateCanImport cycle). The Count <= 1 fast-path
        // also covers the single-row selection case — when the user
        // changes Action on a single selected row there is nothing to
        // batch-apply.
        if (_selectedRows.Count <= 1) return new List<FamilyBatchImportRow>();
        return _selectedRows.Where(r => !ReferenceEquals(r, source)).ToList();
    }
}
