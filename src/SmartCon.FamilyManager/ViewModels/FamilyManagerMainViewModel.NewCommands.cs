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
        StaleCheckMessage = LanguageManager.GetString(StringLocalization.Keys.FM_StaleCheckInProgress) ?? "Проверка…";
        try
        {
            using var _scope = SmartConLogger.BeginScope(
                "StaleDetection",
                ("Method", nameof(CheckCategoryAsync)),
                ("CategoryId", category.CategoryId));

            var doc = _revitContext.GetDocument();
            var results = await _staleDetector.CheckCategoryAsync(
                category.CategoryId, recursive: true, doc, CancellationToken.None)
                .ConfigureAwait(true);

            await ApplyStaleResultsToTreeAsync(results, CancellationToken.None).ConfigureAwait(true);

            var staleCount = results.Count(r => r.IsStale);
            var totalLoaded = results.Count;
            if (totalLoaded == 0)
            {
                StatusMessage = $"«{category.DisplayName}»: в проекте нет загруженных семейств этой категории";
            }
            else
            {
                StatusMessage = $"«{category.DisplayName}»: проверено {totalLoaded}, устарело {staleCount}";
            }
            SmartConLogger.Info(
                $"Check completed: {staleCount} stale of {totalLoaded} families in category '{category.CategoryId}'.");
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

    private bool CanCheckCategory(CategoryNodeViewModel? category) =>
        category != null && !IsStaleCheckInProgress;

    [RelayCommand(CanExecute = nameof(CanCheckFamily))]
    private async Task CheckFamilyAsync(FamilyLeafNodeViewModel? family)
    {
        if (family is null) return;
        IsStaleCheckInProgress = true;
        StaleCheckMessage = LanguageManager.GetString(StringLocalization.Keys.FM_StaleCheckInProgress) ?? "Проверка…";
        try
        {
            using var _scope = SmartConLogger.BeginScope(
                "StaleDetection",
                ("Method", nameof(CheckFamilyAsync)),
                ("CatalogItemId", family.CatalogItemId));

            var doc = _revitContext.GetDocument();
            var familyId = await _awaitableEvent.RaiseAsync(
                _ => FindFamilyInDocument(doc, family.DisplayName)?.Id,
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
            _staleDetector.InvalidateCache();

            StatusMessage = result.IsStale
                ? $"«{family.DisplayName}»: устарело — {ReasonToText(result.Reason)}"
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

    private static string ReasonToText(StaleReason reason) => reason switch
    {
        StaleReason.NoEntityStorage => "нет маркера версии",
        StaleReason.VersionMismatch => "версия в каталоге новее",
        StaleReason.RevitVersionMismatch => "другая версия Revit",
        StaleReason.NotInCatalog => "не найдено в каталоге",
        _ => "обновите семейство",
    };

    private bool CanCheckFamily(FamilyLeafNodeViewModel? family) =>
        family != null && !IsStaleCheckInProgress;

    [RelayCommand(CanExecute = nameof(CanUpdateCategoryOverwrite))]
    private Task UpdateCategoryOverwriteParamsAsync(CategoryNodeViewModel? category)
    {
        if (category is null) return Task.CompletedTask;
        return UpdateCategoryStaleAsync(category, overwriteParameterValues: true);
    }

    private bool CanUpdateCategoryOverwrite(CategoryNodeViewModel? category) =>
        category != null && category.HasStale && !IsStaleCheckInProgress;

    [RelayCommand(CanExecute = nameof(CanUpdateCategoryKeep))]
    private Task UpdateCategoryKeepParamsAsync(CategoryNodeViewModel? category)
    {
        if (category is null) return Task.CompletedTask;
        return UpdateCategoryStaleAsync(category, overwriteParameterValues: false);
    }

    private bool CanUpdateCategoryKeep(CategoryNodeViewModel? category) =>
        category != null && category.HasStale && !IsStaleCheckInProgress;

    private async Task UpdateCategoryStaleAsync(CategoryNodeViewModel category, bool overwriteParameterValues)
    {
        IsStaleCheckInProgress = true;
        StaleCheckMessage = "Обновление…";
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

            var staleIds = snapshot.Results.Values
                .Where(r => r.IsStale)
                .Select(r => r.CatalogItemId)
                .ToList();
            if (staleIds.Count == 0)
            {
                SmartConLogger.Info("UpdateCategory: snapshot has no stale items. [Action: no-op]");
                return;
            }

            var request = new StaleUpdateRequest(staleIds, overwriteParameterValues);
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
                $"Batch update: {result.SuccessCount}/{result.TotalRequested} succeeded. " +
                $"Failed: [{string.Join(", ", result.FailedCatalogItemIds)}]");

            _staleDetector.InvalidateCache();
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
    internal async Task ApplyStaleResultsToTreeAsync(
        IReadOnlyList<StaleCheckResult> results,
        CancellationToken ct)
    {
            var staleIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in results)
            {
                if (r.IsStale) staleIds.Add(r.CatalogItemId);
            }

            // 1) Build reverse map: catalogItemId -> [categoryId, ...] recursively.
            var rootCategories = TreeNodes.OfType<CategoryNodeViewModel>().ToList();
            var adapterRoots = CategoryTreeAdapter.AdaptRoots(rootCategories);
            var categoryMap = _staleAggregator.BuildCatalogToCategoryMap(
                staleIds, adapterRoots);

            // 2) Per category: HasStale + StaleCount.
            foreach (var category in EnumerateAllCategories(rootCategories))
            {
                var hasStale = false;
                var staleCount = 0;
                foreach (var kvp in categoryMap)
                {
                    var catalogId = kvp.Key;
                    var categories = kvp.Value;
                    if (!categories.Contains(category.CategoryId)) continue;
                    if (!staleIds.Contains(catalogId)) continue;
                    hasStale = true;
                    staleCount++;
                }

                category.HasStale = hasStale;
                category.StaleCount = staleCount;
            }

            // 3) Per leaf: IsStale + StaleReason.
            var byCatalog = results.ToDictionary(r => r.CatalogItemId, StringComparer.Ordinal);
            foreach (var leaf in EnumerateAllLeaves(rootCategories))
            {
                if (byCatalog.TryGetValue(leaf.CatalogItemId, out var r))
                {
                    leaf.IsStale = r.IsStale;
                    leaf.StaleReason = r.Reason;
                }
            }

            // 4) Detector cache is preserved so subsequent LoadTreeAsync / Refresh
            //    can re-apply the same IsStale markers to the new TreeNodes.

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

    private static Autodesk.Revit.DB.Family? FindFamilyInDocument(
        Autodesk.Revit.DB.Document doc, string familyName)
    {
        if (doc is null || string.IsNullOrEmpty(familyName)) return null;
        using var collector = new Autodesk.Revit.DB.FilteredElementCollector(doc)
            .OfClass(typeof(Autodesk.Revit.DB.Family));
        foreach (Autodesk.Revit.DB.Family f in collector)
        {
            if (string.Equals(f.Name, familyName, StringComparison.Ordinal)) return f;
        }
        return null;
    }
}
