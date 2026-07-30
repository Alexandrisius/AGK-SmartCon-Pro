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
            var results = await _staleDetector.CheckCategoryAsync(
                subCategoryIds, doc, CancellationToken.None)
                .ConfigureAwait(true);

            await ApplyStaleResultsToTreeAsync(results, CancellationToken.None).ConfigureAwait(true);

            var staleCount = results.Count(r => r.IsStale);
            var totalLoaded = results.Count;
            var totalInTree = EnumerateAllLeaves(TreeNodes.OfType<CategoryNodeViewModel>()).Count();
            if (totalLoaded == 0)
            {
                StatusMessage = totalInTree == 0
                    ? $"«{category.DisplayName}»: в каталоге нет семейств этой категории"
                    : $"«{category.DisplayName}»: семейства в каталоге есть, но ни одно не загружено в проект";
            }
            else
            {
                StatusMessage = $"«{category.DisplayName}»: проверено {totalLoaded}, устарело {staleCount}";
            }
            SmartConLogger.Info(
                $"Check completed: {staleCount} stale of {totalLoaded} families in category '{category.CategoryId}' (subtree={subCategoryIds.Count}).");
        }
        catch (Exception ex)
        {
            StatusMessage = $"«{category.DisplayName}»: ошибка проверки — {ex.Message}";
            SmartConLogger.Warn(
                $"CheckCategoryAsync failed: {ex.Message}. [Action: report to user, retry from context menu]");
        }
        finally
        {
            IsStaleCheckInProgress = false;
            StaleCheckMessage = null;
            NotifyCheckCommands();
        }
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
                    StatusMessage = $"«{family.DisplayName}»: не загружено в проект — сначала загрузите";
                    return;
                }

                family.IsStale = systemResult.IsStale;
                family.StaleReason = systemResult.Reason;
                StatusMessage = systemResult.IsStale
                    ? $"«{family.DisplayName}»: устарело — {systemResult.Reason}"
                    : $"«{family.DisplayName}»: актуально";
                return;
            }

            var familyId = await _awaitableEvent.RaiseAsync(
                _ => _familyFinder.FindByName(doc, family.DisplayName),
                CancellationToken.None).ConfigureAwait(true);

            if (familyId is null)
            {
                StatusMessage = $"«{family.DisplayName}»: не загружено в проект — сначала загрузите";
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
                ? $"«{family.DisplayName}»: устарело — {result.Reason}"
                : $"«{family.DisplayName}»: актуально";
            SmartConLogger.Info(
                $"Check completed: '{family.DisplayName}' IsStale={result.IsStale} Reason={result.Reason}.");
        }
        catch (Exception ex)
        {
            StatusMessage = $"«{family.DisplayName}»: ошибка проверки — {ex.Message}";
            SmartConLogger.Warn(
                $"CheckFamilyAsync failed: {ex.Message}. [Action: report to user, retry from context menu]");
        }
        finally
        {
            IsStaleCheckInProgress = false;
            StaleCheckMessage = null;
            NotifyCheckCommands();
        }
    }

    private bool CanCheckFamily(FamilyLeafNodeViewModel? family) =>
        family != null && !IsStaleCheckInProgress;

    [RelayCommand(CanExecute = nameof(CanUpdateCategoryOverwrite))]
    private Task UpdateCategoryOverwriteParamsAsync(CategoryNodeViewModel? category)
    {
        if (category is null) return Task.CompletedTask;
        return UpdateCategoryStaleAsync(category, overwriteParameterValues: true);
    }

    private bool CanUpdateCategoryOverwrite(CategoryNodeViewModel? category) =>
        category != null && category.HasStale && !IsStaleCheckInProgress && _activeBaseCompatibleWithCurrentDoc;

    [RelayCommand(CanExecute = nameof(CanUpdateCategoryKeep))]
    private Task UpdateCategoryKeepParamsAsync(CategoryNodeViewModel? category)
    {
        if (category is null) return Task.CompletedTask;
        return UpdateCategoryStaleAsync(category, overwriteParameterValues: false);
    }

    private bool CanUpdateCategoryKeep(CategoryNodeViewModel? category) =>
        category != null && category.HasStale && !IsStaleCheckInProgress && _activeBaseCompatibleWithCurrentDoc;

    private async Task UpdateCategoryStaleAsync(CategoryNodeViewModel category, bool overwriteParameterValues)
    {
        // Batch stale-update loads families into the project — same gate as
        // UpdateStale* (Issue #126 read-only database while migrations pending).
        if (!await EnsureDatabaseUpToDateAsync().ConfigureAwait(true)) return;
        IsStaleCheckInProgress = true;
        SmartConLogger.Debug(
            $"UpdateCategoryStaleAsync START: IsStaleCheckInProgress=true (was false). " +
            $"CategoryId={category.CategoryId}, Overwrite={overwriteParameterValues}");
        StaleCheckMessage = LanguageManager.GetString(StringLocalization.Keys.FM_StaleUpdateInProgress);
        try
        {
            using var _scope = SmartConLogger.BeginScope(
                "StaleDetection",
                ("Method", nameof(UpdateCategoryStaleAsync)),
                ("CategoryId", category.CategoryId));

            var snapshot = _staleDetector.GetCachedSnapshot();
            if (snapshot is null)
            {
                SmartConLogger.Info(
                    "UpdateCategory: no snapshot, run Проверить first. " +
                    "[Action: no-op, prompt user to check first]");
                return;
            }

            // 1) Determine which catalog item IDs belong to this category subtree.
            //    Without this filter the batch would include stale families from
            //    every other category checked in the session (e.g. all 42
            //    uncategorized stale families when the user clicks 'Update' on
            //    a 2-family category).
            var rootCategories = TreeNodes.OfType<CategoryNodeViewModel>().ToList();
            var adapterRoots = CategoryTreeAdapter.AdaptRoots(rootCategories);
            var subCategoryIds = new HashSet<string>(
                ExpandCategorySubtree(category),
                StringComparer.Ordinal);
            var allStaleIds = snapshot.Results
                .Where(r => r.Value.IsStale)
                .Select(r => r.Key)
                .ToList();
            var categoryMap = _staleAggregator.BuildCatalogToCategoryMap(
                allStaleIds, adapterRoots);
            var staleIdsInSubtree = StaleSnapshotLogic.FilterStaleBySubtree(
                allStaleIds, categoryMap, subCategoryIds);
            if (staleIdsInSubtree.Count == 0)
            {
                SmartConLogger.Info(
                    $"UpdateCategory: no stale items in '{category.CategoryId}' (snapshot has " +
                    $"{snapshot.Results.Count(r => r.Value.IsStale)} stale total). " +
                    "[Action: no-op]");
                return;
            }

            var request = new StaleUpdateRequest(staleIdsInSubtree, overwriteParameterValues);
            var progress = new Progress<StaleBatchUpdateProgress>(p =>
            {
                StaleCheckMessage = $"{p.Completed}/{p.Total}: {p.CurrentFamilyName}";
            });

            var result = await _staleUpdater.UpdateBatchAsync(request, progress, CancellationToken.None)
                .ConfigureAwait(true);

            var modeText = overwriteParameterValues ? "с перезаписью параметров" : "с сохранением параметров";
            if (result.FailedCount == 0)
            {
                StatusMessage = $"«{category.DisplayName}»: обновлено {result.SuccessCount} из {result.TotalRequested} ({modeText})";
            }
            else
            {
                StatusMessage = $"«{category.DisplayName}»: обновлено {result.SuccessCount} из {result.TotalRequested}, ошибок: {result.FailedCount}";
            }
            SmartConLogger.Info(
                $"Batch update in '{category.CategoryId}': {result.SuccessCount}/{result.TotalRequested} succeeded. " +
                $"Failed: [{string.Join(", ", result.FailedCatalogItemIds)}]");

            // Remove only the successfully updated items from the snapshot so the
            // next Check re-evaluates them from scratch. Other categories' markers
            // (and the families that FAILED to update) stay intact.
            _staleDetector.MarkUpdated(result.SuccessCatalogItemIds);
            await LoadTreeAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = $"«{category.DisplayName}»: ошибка обновления — {ex.Message}";
            SmartConLogger.Warn(
                $"UpdateCategoryStaleAsync failed: {ex.Message}. " +
                "[Action: report to user, retry from context menu]");
        }
        finally
        {
            IsStaleCheckInProgress = false;
            StaleCheckMessage = null;
            NotifyCheckCommands();
        }
    }

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
        //    Returns null only if the cache has been invalidated (DB switch,
        //    explicit InvalidateCache) — in that case we have nothing to apply.
        var merged = _staleDetector.GetMergedSnapshot(results);
        if (merged is null) return;

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

        await Task.CompletedTask;
    }

    private void NotifyCheckCommands()
    {
        CheckCategoryCommand.NotifyCanExecuteChanged();
        CheckFamilyCommand.NotifyCanExecuteChanged();
        UpdateCategoryOverwriteParamsCommand.NotifyCanExecuteChanged();
        UpdateCategoryKeepParamsCommand.NotifyCanExecuteChanged();
    }

    private static IEnumerable<CategoryNodeViewModel> EnumerateAllCategories(
        IEnumerable<CategoryNodeViewModel> roots)
    {
        var stack = new Stack<CategoryNodeViewModel>(roots);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            foreach (var child in node.Children.OfType<CategoryNodeViewModel>())
            {
                stack.Push(child);
            }
        }
    }

    private static IEnumerable<FamilyLeafNodeViewModel> EnumerateAllLeaves(
        IEnumerable<CategoryNodeViewModel> roots)
    {
        var stack = new Stack<CategoryNodeViewModel>(roots);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            foreach (var child in node.Children)
            {
                if (child is FamilyLeafNodeViewModel leaf) yield return leaf;
                else if (child is CategoryNodeViewModel sub) stack.Push(sub);
            }
        }
    }
}
