using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// Partial of <see cref="FamilyManagerMainViewModel"/> with the catalog
/// compliance commands (#259, «Проверить → Правила»): the catalog item vs the
/// effective validation rules of its category. Pure DB — no Revit document
/// required (unlike «Актуальность», which stays a separate command with a
/// separate verdict model; the two never mix). The commands share the
/// <see cref="IsStaleCheckInProgress"/> guard and the pane progress bar (#256)
/// with the stale checks: only one check-ish operation runs at a time.
/// </summary>
public sealed partial class FamilyManagerMainViewModel
{
    [RelayCommand(CanExecute = nameof(CanCheckCategoryRules))]
    private async Task CheckCategoryRulesAsync(CategoryNodeViewModel? category)
    {
        if (category is null) return;
        IsStaleCheckInProgress = true;
        StaleCheckMessage = LanguageManager.GetString(StringLocalization.Keys.FM_RuleCheckInProgress)
            ?? "Проверка правил…";
        BeginProgress();
        try
        {
            using var _scope = SmartConLogger.BeginScope(
                "CatalogCompliance",
                ("Method", nameof(CheckCategoryRulesAsync)),
                ("CategoryId", category.CategoryId));

            // Same subtree contract as «Актуальность»: the category command
            // covers every nested subcategory and their items.
            var subCategoryIds = ExpandCategorySubtree(category);

            var progress = new Progress<ComplianceCheckProgress>(OnComplianceCheckProgress);
            var results = await _complianceService.CheckCategoriesAsync(
                subCategoryIds, progress, CancellationToken.None)
                .ConfigureAwait(true);

            ApplyComplianceResultsToTree(results);

            var failCount = results.Count(r => r.Status == ComplianceStatus.Fail);
            var cannotVerifyCount = results.Count(r => r.Status == ComplianceStatus.CannotVerify);
            StatusMessage = results.Count == 0
                ? $"«{category.DisplayName}»: в категории нет элементов каталога"
                : $"«{category.DisplayName}»: проверено правил — {results.Count}, нарушений {failCount}"
                  + (cannotVerifyCount > 0 ? $", без данных {cannotVerifyCount}" : string.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = $"«{category.DisplayName}»: ошибка проверки правил — {ex.Message}";
            SmartConLogger.Warn(
                $"CheckCategoryRulesAsync failed: {ex.Message}. [Action: report to user, retry from context menu «Проверить → Правила»]");
        }
        finally
        {
            IsStaleCheckInProgress = false;
            StaleCheckMessage = null;
            ResetProgress();
            NotifyCheckCommands();
        }
    }

    private bool CanCheckCategoryRules(CategoryNodeViewModel? category) =>
        category != null && !IsStaleCheckInProgress;

    [RelayCommand(CanExecute = nameof(CanCheckFamilyRules))]
    private async Task CheckFamilyRulesAsync(FamilyLeafNodeViewModel? family)
    {
        if (family is null) return;
        IsStaleCheckInProgress = true;
        StaleCheckMessage = LanguageManager.GetString(StringLocalization.Keys.FM_RuleCheckInProgress)
            ?? "Проверка правил…";
        BeginProgress();
        try
        {
            using var _scope = SmartConLogger.BeginScope(
                "CatalogCompliance",
                ("Method", nameof(CheckFamilyRulesAsync)),
                ("CatalogItemId", family.CatalogItemId));

            var result = await _complianceService.CheckItemAsync(family.CatalogItemId, CancellationToken.None)
                .ConfigureAwait(true);

            ApplyComplianceResultsToTree([result]);

            StatusMessage = result.Status switch
            {
                ComplianceStatus.Fail =>
                    $"«{family.DisplayName}»: нарушений правил — {result.Violations.Count} (отчёт — по клику на красный щит)",
                ComplianceStatus.CannotVerify =>
                    $"«{family.DisplayName}»: нет данных атрибутов — выполните «Обновить базу»",
                _ => result.RuleCount == 0
                    ? $"«{family.DisplayName}»: для категории не заданы правила"
                    : $"«{family.DisplayName}»: правила категории выполнены ({result.RuleCount})",
            };
        }
        catch (Exception ex)
        {
            StatusMessage = $"«{family.DisplayName}»: ошибка проверки правил — {ex.Message}";
            SmartConLogger.Warn(
                $"CheckFamilyRulesAsync failed: {ex.Message}. [Action: report to user, retry from context menu «Проверить → Правила»]");
        }
        finally
        {
            IsStaleCheckInProgress = false;
            StaleCheckMessage = null;
            ResetProgress();
            NotifyCheckCommands();
        }
    }

    private bool CanCheckFamilyRules(FamilyLeafNodeViewModel? family) =>
        family != null && !IsStaleCheckInProgress;

    /// <summary>
    /// Per-item feed of <see cref="SmartCon.Core.Services.Interfaces.ICatalogComplianceService.CheckCategoriesAsync"/>
    /// — renders the pane progress bar + «Проверка правил X из Y — имя» status
    /// text. <see cref="Progress{T}"/> marshals the callback to the UI context.
    /// </summary>
    private void OnComplianceCheckProgress(ComplianceCheckProgress p)
    {
        StaleCheckMessage = string.Format(
            LanguageManager.GetString(StringLocalization.Keys.FM_RuleCheck_ProgressFormat)
                ?? "Проверка правил {0} из {1} — {2}",
            p.Completed, p.Total, p.CurrentItemName);
        ReportProgress(p.Completed, p.Total);
    }

    /// <summary>
    /// Paints the merged compliance snapshot onto the tree: per-leaf verdict
    /// (badge + notices) and the recursive per-category Fail roll-up. Called
    /// after every «Правила» run and after each tree rebuild (the session
    /// snapshot survives rebuilds exactly like the stale snapshot).
    /// <para>
    /// A verdict applies ONLY while its <see cref="ComplianceCheckResult.CategoryId"/>
    /// matches the leaf's current category — after a DnD/picker move the old
    /// verdict was computed under other rules and must disappear instead of
    /// lying until the next check.
    /// </para>
    /// </summary>
    internal void ApplyComplianceResultsToTree(IReadOnlyList<ComplianceCheckResult> newResults)
    {
        var merged = _complianceService.GetMergedSnapshot(newResults);
        var rootCategories = TreeNodes.OfType<CategoryNodeViewModel>().ToList();

        foreach (var leaf in EnumerateAllLeaves(rootCategories))
        {
            if (merged.Results.TryGetValue(leaf.CatalogItemId, out var result)
                && string.Equals(
                    result.CategoryId ?? string.Empty,
                    leaf.CategoryId ?? string.Empty,
                    StringComparison.Ordinal))
            {
                leaf.RuleViolationCount = result.Violations.Count;
                leaf.ComplianceStatus = result.Status;
            }
            else if (leaf.ComplianceStatus != ComplianceStatus.NotChecked)
            {
                leaf.RuleViolationCount = 0;
                leaf.ComplianceStatus = ComplianceStatus.NotChecked;
            }
        }

        foreach (var root in rootCategories)
        {
            CountRuleViolationsRecursive(root);
        }
    }

    /// <summary>Recursive Fail roll-up: every category gets the number of
    /// violating leaves in its whole subtree (same semantics as the stale
    /// roll-up).</summary>
    private static int CountRuleViolationsRecursive(CategoryNodeViewModel category)
    {
        var count = 0;
        foreach (var child in category.Children)
        {
            if (child is FamilyLeafNodeViewModel leaf)
            {
                if (leaf.HasRuleViolations) count++;
            }
            else if (child is CategoryNodeViewModel sub)
            {
                count += CountRuleViolationsRecursive(sub);
            }
        }
        category.RuleViolationCount = count;
        return count;
    }

    /// <summary>
    /// «Открыть отчёт о проверке» action of the status-details dialog (#259):
    /// rebuilds the violation report from the session snapshot — the existing
    /// <see cref="ValidationReportViewModel"/> dialog without modifications
    /// (type / attribute / condition / expected / actual + summary + copy).
    /// </summary>
    internal void OpenComplianceReport(FamilyLeafNodeViewModel leaf)
    {
        var snapshot = _complianceService.GetCachedSnapshot();
        if (snapshot is null
            || !snapshot.Results.TryGetValue(leaf.CatalogItemId, out var result)
            || result.Status != ComplianceStatus.Fail)
        {
            SmartConLogger.Info(
                $"OpenComplianceReport: no Fail verdict in the session snapshot for '{leaf.DisplayName}' — nothing to show. " +
                "[Action: запустите «Проверить → Правила» повторно]");
            return;
        }

        var report = new FamilyValidationReport(false, result.Violations, result.RulesEvaluated);
        var reportVm = _viewModelFactory.CreateValidationReportViewModel(
            leaf.DisplayName, leaf.CategoryPath ?? string.Empty, null, report, result.RuleCount);
        _dialogService.ShowValidationReport(reportVm);
    }

    /// <summary>
    /// True when the session snapshot holds a Fail verdict for the leaf that
    /// is still valid for its CURRENT category — the guard of the
    /// «Открыть отчёт о проверке» action in the status-details dialog.
    /// </summary>
    internal bool HasComplianceFailVerdict(FamilyLeafNodeViewModel leaf) =>
        leaf.HasRuleViolations
        && _complianceService.GetCachedSnapshot()?.Results.TryGetValue(leaf.CatalogItemId, out var result) == true
        && result.Status == ComplianceStatus.Fail
        && string.Equals(
            result.CategoryId ?? string.Empty,
            leaf.CategoryId ?? string.Empty,
            StringComparison.Ordinal);
}
