using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.Stale;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// Partial of <see cref="FamilyManagerMainViewModel"/> with Phase 24 stale-detection commands
/// (ADR-030, Issue #69). All commands are guarded by <see cref="IsStaleCheckInProgress"/>
/// so the user cannot fire a second check while the first is still running.
/// </summary>
public sealed partial class FamilyManagerMainViewModel
{
    [RelayCommand(CanExecute = nameof(CanCheckCategory))]
    private async Task CheckCategoryAsync(CategoryNodeViewModel? category)
    {
        if (category is null) return;
        IsStaleCheckInProgress = true;
        StaleCheckMessage = LanguageManager.GetString(StringLocalization.Keys.FM_StaleCheckInProgress);
        var progressRunId = BeginProgress();
        try
        {
            using var _scope = SmartConLogger.BeginScope(
                "StaleDetection",
                ("Method", nameof(CheckCategoryAsync)),
                ("CategoryId", category.CategoryId));

            // Expand the category subtree to a flat list of category IDs so the
            // detector sees one consistent contract (no recursive flag, no tree access).
            var subCategoryIds = ExpandCategorySubtree(category);

            var doc = _revitContext.GetDocument();
            var progress = new Progress<StaleCheckProgress>(p => OnStaleCheckProgress(progressRunId, p));
            var results = await _staleDetector.CheckCategoryAsync(
                subCategoryIds, doc, CancellationToken.None, progress)
                .ConfigureAwait(true);

            await ApplyStaleResultsToTreeAsync(results, CancellationToken.None).ConfigureAwait(true);

            var staleCount = results.Count(r => r.IsStale);
            var totalLoaded = results.Count;
            var totalInTree = EnumerateAllLeaves(TreeNodes.OfType<CategoryNodeViewModel>()).Count();
            if (totalLoaded == 0)
            {
                StatusMessage = totalInTree == 0
                    ? string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_Status_CategoryNoFamilies)
                            ?? "«{0}»: в каталоге нет семейств этой категории",
                        category.DisplayName)
                    : string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_Status_CategoryNoneLoaded)
                            ?? "«{0}»: семейства в каталоге есть, но ни одно не загружено в проект",
                        category.DisplayName);
            }
            else
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_Status_CategoryCheckResult)
                        ?? "«{0}»: проверено {1}, устарело {2}",
                    category.DisplayName, totalLoaded, staleCount);
            }
            SmartConLogger.Info(
                $"Check completed: {staleCount} stale of {totalLoaded} families in category '{category.CategoryId}' (subtree={subCategoryIds.Count}).");
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_Status_CheckError)
                    ?? "«{0}»: ошибка проверки — {1}",
                category.DisplayName, ex.Message);
            SmartConLogger.Warn(
                $"CheckCategoryAsync failed: {ex.Message}. [Action: report to user, retry from context menu]");
        }
        finally
        {
            IsStaleCheckInProgress = false;
            StaleCheckMessage = null;
            CompleteProgress();
            NotifyCheckCommands();
        }
    }

    /// <summary>
    /// Per-family feed of <see cref="IStaleDetector.CheckCategoryAsync"/> —
    /// renders the pane progress bar + «Проверка X из Y — имя» status text.
    /// <see cref="Progress{T}"/> POSTS the callback: the run-id guard keeps
    /// reports arriving after a fast run finished (they fill the bar during
    /// the completion hold) and drops superseded/already-hidden ones —
    /// see <see cref="OnComplianceCheckProgress"/>.
    /// </summary>
    private void OnStaleCheckProgress(int runId, StaleCheckProgress p)
    {
        if (!IsProgressReportCurrent(runId))
        {
            SmartConLogger.Debug(
                $"OnStaleCheckProgress: dropped stale report {p.Completed}/{p.Total} of run {runId} (current {_progressRunId})");
            return;
        }
        StaleCheckMessage = string.Format(
            LanguageManager.GetString(StringLocalization.Keys.FM_StaleCheck_ProgressFormat)
                ?? "Проверка {0} из {1} — {2}",
            p.Completed, p.Total, p.CurrentFamilyName);
        ReportProgress(p.Completed, p.Total);
    }

    private static IReadOnlyList<string> ExpandCategorySubtree(CategoryNodeViewModel root)
    {
        var result = new List<string> { root.CategoryId };
        var stack = new Stack<CategoryNodeViewModel>();
        foreach (var child in root.Children.OfType<CategoryNodeViewModel>())
        {
            stack.Push(child);
        }
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            result.Add(node.CategoryId);
            foreach (var child in node.Children.OfType<CategoryNodeViewModel>())
            {
                stack.Push(child);
            }
        }
        return result;
    }

    private bool CanCheckCategory(CategoryNodeViewModel? category) =>
        category != null && !IsStaleCheckInProgress;

    [RelayCommand(CanExecute = nameof(CanCheckFamily))]
    private async Task CheckFamilyAsync(FamilyLeafNodeViewModel? family)
    {
        if (family is null) return;
        IsStaleCheckInProgress = true;
        StaleCheckMessage = LanguageManager.GetString(StringLocalization.Keys.FM_StaleCheckInProgress);
        BeginProgress();
        try
        {
            using var _scope = SmartConLogger.BeginScope(
                "StaleDetection",
                ("Method", nameof(CheckFamilyAsync)),
                ("CatalogItemId", family.CatalogItemId));

            var doc = _revitContext.GetDocument();

            // Issue #104: system leaves are matched by their types'
            // ElementType markers, not by a Family element.
            if (family.FamilySource == "system")
            {
                var systemResult = await _staleDetector.CheckSystemFamilyAsync(
                    family.CatalogItemId, family.DisplayName, doc, CancellationToken.None)
                    .ConfigureAwait(true);

                if (systemResult is null)
                {
                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_Status_FamilyNotLoaded)
                            ?? "«{0}»: не загружено в проект — сначала загрузите",
                        family.DisplayName);
                    return;
                }

                family.IsStale = systemResult.IsStale;
                family.StaleReason = systemResult.Reason;
                StatusMessage = systemResult.IsStale
                    ? string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_Status_FamilyStaleReason)
                            ?? "«{0}»: устарело — {1}",
                        family.DisplayName, systemResult.Reason)
                    : string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_Status_FamilyUpToDate)
                            ?? "«{0}»: актуально",
                        family.DisplayName);
                // The detector already upserted the fresh result (and the
                // per-type verdicts) into the snapshot — apply the merged
                // picture so the orange type dots and the category rollup
                // repaint, exactly like after a category Check.
                await ApplyStaleResultsToTreeAsync([], CancellationToken.None)
                    .ConfigureAwait(true);
                return;
            }

            var familyId = await _awaitableEvent.RaiseAsync(
                _ => _familyFinder.FindByName(doc, family.DisplayName),
                CancellationToken.None).ConfigureAwait(true);

            if (familyId is null)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_Status_FamilyNotLoaded)
                        ?? "«{0}»: не загружено в проект — сначала загрузите",
                    family.DisplayName);
                SmartConLogger.Info(
                    $"CheckFamily: family '{family.DisplayName}' not loaded in document. " +
                    "[Action: skipped, user can load then re-check]");
                return;
            }

            var result = await _staleDetector.CheckFamilyAsync(
                family.CatalogItemId, family.DisplayName, doc, familyId, CancellationToken.None)
                .ConfigureAwait(true);

            family.IsStale = result.IsStale;
            family.StaleReason = result.Reason;
            // Note: CheckFamilyAsync already updates the snapshot for this family only;
            // no InvalidateCache — other categories' stale markers must stay intact.

            StatusMessage = result.IsStale
                ? string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_Status_FamilyStaleReason)
                        ?? "«{0}»: устарело — {1}",
                    family.DisplayName, result.Reason)
                : string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_Status_FamilyUpToDate)
                        ?? "«{0}»: актуально",
                    family.DisplayName);
            SmartConLogger.Info(
                $"Check completed: '{family.DisplayName}' IsStale={result.IsStale} Reason={result.Reason}.");
            // Same repaint as the system path above: the snapshot holds the
            // fresh result — apply it so the category rollup stays truthful.
            await ApplyStaleResultsToTreeAsync([], CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_Status_CheckError)
                    ?? "«{0}»: ошибка проверки — {1}",
                family.DisplayName, ex.Message);
            SmartConLogger.Warn(
                $"CheckFamilyAsync failed: {ex.Message}. [Action: report to user, retry from context menu]");
        }
        finally
        {
            IsStaleCheckInProgress = false;
            StaleCheckMessage = null;
            CompleteProgress();
            NotifyCheckCommands();
        }
    }

    private bool CanCheckFamily(FamilyLeafNodeViewModel? family) =>
        family != null && !IsStaleCheckInProgress;

    /// <summary>
    /// Updates <c>HasStale</c> / <c>StaleCount</c> on every category in the tree
    /// and <c>IsStale</c> / <c>StaleReason</c> on every affected leaf. Called after
    /// <see cref="IStaleDetector.CheckCategoryAsync"/>.
    /// </summary>
    /// <remarks>
    /// The tree's stale indicators must reflect the COMPLETE snapshot, not just the
    /// families from the latest Check call. Otherwise the second stale category
    /// loses its marker the moment the user runs Check on the first one. We use
    /// <see cref="IStaleDetector.GetMergedSnapshot"/> to fold the new results into
    /// the existing cache and roll up the per-category stats from there.
    /// </remarks>
    internal async Task ApplyStaleResultsToTreeAsync(
        IReadOnlyList<StaleCheckResult> results,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // 0) Get the complete picture: existing snapshot + fresh results.
        //    A cold/invalidated cache is no longer a silent no-op (#220):
        //    GetMergedSnapshot starts from the empty snapshot, so the
        //    post-DnD tree rebuild always recomputes badges.
        var merged = _staleDetector.GetMergedSnapshot(results);

        // 1) Collect all stale IDs from the merged snapshot — covers every
        //    category that was checked in this session.
        var staleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in merged.Results.Values)
        {
            if (r.IsStale) staleIds.Add(r.CatalogItemId);
        }

        // 2) Build reverse map: catalogItemId -> [categoryId, ...] recursively.
        var rootCategories = TreeNodes.OfType<CategoryNodeViewModel>().ToList();
        var adapterRoots = CategoryTreeAdapter.AdaptRoots(rootCategories);
        var categoryMap = _staleAggregator.BuildCatalogToCategoryMap(
            staleIds, adapterRoots);

        // 3) Per category: HasStale + StaleCount from the merged snapshot.
        var allCategories = EnumerateAllCategories(rootCategories).ToList();
        var perCategory = _staleAggregator.AggregateByCategory(
            merged.Results.Values.ToList(), categoryMap, staleIds);
        foreach (var category in allCategories)
        {
            if (perCategory.TryGetValue(category.CategoryId, out var stats))
            {
                category.HasStale = stats.HasStale;
                category.StaleCount = stats.StaleCount;
            }
            else
            {
                category.HasStale = false;
                category.StaleCount = 0;
            }
        }

        // 4) Per leaf: IsStale + StaleReason from the merged snapshot.
        //    (Use merged, not just new results — leaves in OTHER categories that
        //    were checked in an earlier Check must keep their markers.)
        foreach (var leaf in EnumerateAllLeaves(rootCategories))
        {
            if (merged.Results.TryGetValue(leaf.CatalogItemId, out var r))
            {
                leaf.IsStale = r.IsStale;
                leaf.StaleReason = r.Reason;
            }
        }

        // 5) #187: per-type orange dots follow the freshly checked markers.
        ApplySystemTypeStaleMaps();

        await Task.CompletedTask;
    }

    private void NotifyCheckCommands()
    {
        CheckCategoryCommand.NotifyCanExecuteChanged();
        CheckFamilyCommand.NotifyCanExecuteChanged();
        CheckCategoryRulesCommand.NotifyCanExecuteChanged();
        CheckFamilyRulesCommand.NotifyCanExecuteChanged();
        UpdateCategoryOverwriteParamsCommand.NotifyCanExecuteChanged();
        UpdateCategoryKeepParamsCommand.NotifyCanExecuteChanged();
    }

}
