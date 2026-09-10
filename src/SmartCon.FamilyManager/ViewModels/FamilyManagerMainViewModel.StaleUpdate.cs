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

public sealed partial class FamilyManagerMainViewModel
{
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
        var progressRunId = BeginProgress();
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
                // Run-id guard — see OnComplianceCheckProgress.
                if (!IsProgressReportCurrent(progressRunId))
                {
                    SmartConLogger.Debug(
                        $"UpdateCategoryStale progress: dropped stale report {p.Completed}/{p.Total} of run {progressRunId} (current {_progressRunId})");
                    return;
                }
                StaleCheckMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_StaleUpdate_ProgressFormat)
                        ?? "Обновление {0} из {1} — {2}",
                    p.Completed, p.Total, p.CurrentFamilyName);
                ReportProgress(p.Completed, p.Total);
            });

            var result = await _staleUpdater.UpdateBatchAsync(request, progress, CancellationToken.None)
                .ConfigureAwait(true);

            var modeText = overwriteParameterValues
                ? LanguageManager.GetString(StringLocalization.Keys.FM_Status_ModeOverwriteParams)
                    ?? "с перезаписью параметров"
                : LanguageManager.GetString(StringLocalization.Keys.FM_Status_ModeKeepParams)
                    ?? "с сохранением параметров";
            if (result.FailedCount == 0)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_Status_CategoryUpdateResult)
                        ?? "«{0}»: обновлено {1} из {2} ({3})",
                    category.DisplayName, result.SuccessCount, result.TotalRequested, modeText);
            }
            else
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_Status_CategoryUpdateResultErrors)
                        ?? "«{0}»: обновлено {1} из {2}, ошибок: {3}",
                    category.DisplayName, result.SuccessCount, result.TotalRequested, result.FailedCount);
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
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_Status_UpdateError)
                    ?? "«{0}»: ошибка обновления — {1}",
                category.DisplayName, ex.Message);
            SmartConLogger.Warn(
                $"UpdateCategoryStaleAsync failed: {ex.Message}. " +
                "[Action: report to user, retry from context menu]");
        }
        finally
        {
            IsStaleCheckInProgress = false;
            StaleCheckMessage = null;
            CompleteProgress();
            NotifyCheckCommands();
        }
    }
}
