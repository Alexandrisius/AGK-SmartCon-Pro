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
    /// <summary>
    /// #241: evaluates the assignment rules for EVERY row and applies the
    /// matched category only to the eligible ones (Status == New,
    /// provenance == None, not gate-blocked — Existing/Duplicate and
    /// Manual/Command rows are NEVER re-categorized automatically). Every
    /// row gets the recommendation state (ids + display paths) that drives
    /// the category-column warning icon — including Existing/Duplicate
    /// rows whose current category differs from the rules' recommendation.
    /// MUST run on the UI thread.
    /// </summary>
    private List<FamilyBatchImportRow> ApplyAutoAssignment(IEnumerable<FamilyBatchImportRow> candidates)
    {
        using var _scope = SmartConLogger.BeginScope("AutoAssign",
            ("Method", nameof(ApplyAutoAssignment)));
        var assigned = new List<FamilyBatchImportRow>();
        var recommended = 0;
        foreach (var row in candidates)
        {
            if (_isClosing)
            {
                SmartConLogger.Debug("BatchImport.AutoAssign: application skipped — dialog is closing");
                break;
            }

            CategoryAutoAssignResult result;
            try
            {
                result = _autoAssignService!.Evaluate(
                    _autoAssignRules!, row.LoadableSnapshot, row.SystemSnapshot, row.FileName);
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"BatchImport.AutoAssign: evaluation failed for '{row.FileName}': {ex.Message} " +
                    "[Action: проверьте логи; строка остаётся в «Без категории»]");
                continue;
            }

            ApplyRecommendation(row, result);

            if (result.Outcome == CategoryAutoAssignOutcome.Matched
                && row.Status == FamilyBatchImportStatus.New
                && row.CategoryProvenance == CategoryProvenance.None
                && !row.IsGateBlocked)
            {
                // Re-check the icon state after the assignment: the current
                // category now equals the recommendation → icon hidden.
                AssignAutoRuleCategory(row, result.CategoryId!);
                assigned.Add(row);
            }
            else if (result.Outcome != CategoryAutoAssignOutcome.NoMatch)
            {
                recommended++;
            }
        }

        if (assigned.Count > 0 || recommended > 0)
        {
            SmartConLogger.Info(
                $"Auto-assign: {assigned.Count} row(s) assigned, {recommended} row(s) recommended (not auto-applied) of {Items.Count}");
            UpdateCanImport();
        }

        return assigned;
    }

    /// <summary>
    /// #241: stores the rules' recommendation on the row (drives the
    /// category-column warning icon regardless of the row's eligibility
    /// for automatic assignment).
    /// </summary>
    private void ApplyRecommendation(FamilyBatchImportRow row, CategoryAutoAssignResult result)
    {
        switch (result.Outcome)
        {
            case CategoryAutoAssignOutcome.Matched when result.CategoryId is not null:
                row.RecommendedCategoryIds = [result.CategoryId];
                row.RecommendedCategoryPaths = [ResolveCategoryPath(result.CategoryId)];
                break;
            case CategoryAutoAssignOutcome.Ambiguous:
                row.RecommendedCategoryIds = result.CandidateCategoryIds;
                row.RecommendedCategoryPaths = ResolveCategoryPaths(result.CandidateCategoryIds);
                SmartConLogger.Debug(
                    $"BatchImport.AutoAssign: '{row.FileName}' recommendation — {result.CandidateCategoryIds.Count} categories match");
                break;
            default:
                row.RecommendedCategoryIds = null;
                row.RecommendedCategoryPaths = null;
                break;
        }
    }

    /// <summary>
    /// #241: re-evaluates ONE row's assignment rules (rename path). Runs on
    /// the UI thread with the cached rules; a matched category fires the
    /// row's CategoryChanged chain (provenance BEFORE path — same contract
    /// as <c>ApplyNameChangeResult</c>).
    /// </summary>
    private void TryAutoAssignRow(FamilyBatchImportRow row)
    {
        if (_autoAssignRules is not { HasRules: true })
        {
            return;
        }

        CategoryAutoAssignResult result;
        try
        {
            result = _autoAssignService!.Evaluate(
                _autoAssignRules, row.LoadableSnapshot, row.SystemSnapshot, row.FileName);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"BatchImport.AutoAssign: re-evaluation failed for '{row.FileName}': {ex.Message} " +
                "[Action: проверьте логи; рекомендация строки может быть неактуальной]");
            return;
        }

        ApplyRecommendation(row, result);

        if (result.Outcome == CategoryAutoAssignOutcome.Matched
            && result.CategoryId is not null
            && row.Status == FamilyBatchImportStatus.New
            && row.CategoryProvenance == CategoryProvenance.None
            && !row.IsGateBlocked)
        {
            AssignAutoRuleCategory(row, result.CategoryId);
        }
    }

    private void AssignAutoRuleCategory(FamilyBatchImportRow row, string categoryId)
    {
        // Provenance BEFORE the path: the CategoryChanged batch-apply
        // (fired by the path setter) must observe the row's new provenance.
        row.CategoryProvenance = CategoryProvenance.AutoRule;
        row.TargetCategoryId = categoryId;
        row.TargetCategoryPath = ResolveCategoryPath(categoryId);
        row.CategoryFlashToken++;
        SmartConLogger.Debug(
            $"BatchImport.AutoAssign: '{row.FileName}' -> '{row.TargetCategoryPath}' (provenance=AutoRule)");
    }

    private string ResolveCategoryPath(string categoryId) =>
        _autoAssignRules?.CategoryPathsById.TryGetValue(categoryId, out var path) == true
            ? path
            : categoryId;

    private List<string> ResolveCategoryPaths(IReadOnlyList<string> categoryIds)
    {
        var paths = new List<string>(categoryIds.Count);
        foreach (var id in categoryIds)
        {
            paths.Add(ResolveCategoryPath(id));
        }

        return paths;
    }
}
