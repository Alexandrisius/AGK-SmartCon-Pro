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
    /// <summary>
    /// Issue #185: automatic stale check right after a catalog import
    /// completes ("Импорт активного файла" / "Импорт выделенных элементов").
    /// The user must SEE immediately that the work project's types are now
    /// outdated relative to the freshly imported catalog version — otherwise
    /// the "Обновить" command (ADR-061 sync) stays undiscovered.
    /// </summary>
    /// <remarks>
    /// Runs once per batch (never per item). The active document at this
    /// point is the WORK project (after the #186 mini-project close), whose
    /// types carry markers of the PREVIOUS catalog version — the check
    /// honestly flags them stale. Items not present in the project are
    /// skipped (not stale — simply not loaded).
    /// </remarks>
    internal async Task RunPostImportStaleCheckAsync(IReadOnlyList<ImportedCatalogItem> items)
    {
        if (items.Count == 0) return;
        // #259: the imported versions just passed the import gate (validated on
        // entry) — their previous compliance verdicts are outdated. Drop them
        // BEFORE the concurrency guard: invalidation must happen even when a
        // check is already running and the stale cycle below is skipped.
        _complianceService.InvalidateItems(
            items.Select(i => i.CatalogItemId)
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray());
        // M1 (review): the cycle must hold the same guard as a manual Check —
        // otherwise a concurrent "Обновить"/"Проверить" would race the merge
        // and a stale badge could land on a just-updated family.
        if (IsStaleCheckInProgress)
        {
            SmartConLogger.Debug("Post-import stale check skipped — another check is already running");
            return;
        }
        IsStaleCheckInProgress = true;
        // XAML contract: while IsStaleCheckInProgress is set the status line
        // shows StaleCheckMessage (and StatusMessage is collapsed) — the
        // message must be non-null, otherwise the user sees a blank bar and
        // disabled commands with no reason.
        StaleCheckMessage = LanguageManager.GetString(StringLocalization.Keys.FM_StaleCheckInProgress);
        BeginProgress();
        using var _scope = SmartConLogger.BeginScope(
            "StaleDetection",
            ("Method", nameof(RunPostImportStaleCheckAsync)),
            ("Count", items.Count));
        try
        {
            Autodesk.Revit.DB.Document? doc = null;
            try
            {
                doc = _revitContext.GetDocument();
            }
            catch (Exception)
            {
                // L1 (review): RevitContext.GetDocument throws (NRE) when no
                // document is active — that is a silent skip, not a Warn.
                SmartConLogger.Debug("Post-import stale check skipped — no active document");
                return;
            }

            var results = new List<StaleCheckResult>();
            var seenItemIds = new HashSet<string>(StringComparer.Ordinal);
            var total = items.Count;
            var done = 0;
            foreach (var item in items)
            {
                // The bar advances on EVERY batch row (including duplicate
                // skips) so the progress stays monotonic for the user.
                done++;
                StaleCheckMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_StaleCheck_ProgressFormat)
                        ?? "Проверка {0} из {1} — {2}",
                    done, total, item.DisplayName);
                ReportProgress(done, total);

                // L3 (review): the batch may carry two rows for one catalog
                // item (cross-name duplicate + MakeActive) — check it once.
                if (!seenItemIds.Add(item.CatalogItemId)) continue;

                if (item.FamilySource == "system")
                {
                    var systemResult = await _staleDetector.CheckSystemFamilyAsync(
                        item.CatalogItemId, item.DisplayName, doc, CancellationToken.None)
                        .ConfigureAwait(true);
                    if (systemResult is not null) results.Add(systemResult);
                }
                else
                {
                    var familyId = await _awaitableEvent.RaiseAsync(
                        _ => _familyFinder.FindByName(doc, item.DisplayName),
                        CancellationToken.None).ConfigureAwait(true);
                    if (familyId is null) continue;

                    results.Add(await _staleDetector.CheckFamilyAsync(
                        item.CatalogItemId, item.DisplayName, doc, familyId, CancellationToken.None)
                        .ConfigureAwait(true));
                }
            }

            if (results.Count == 0)
            {
                SmartConLogger.Info(
                    $"Post-import stale check: none of {items.Count} imported item(s) are present in the active project");
                return;
            }

            await ApplyStaleResultsToTreeAsync(results, CancellationToken.None).ConfigureAwait(true);

            var staleCount = results.Count(r => r.IsStale);
            SmartConLogger.Info(
                $"Post-import stale check: {staleCount} stale of {results.Count} checked item(s)");
            if (staleCount > 0)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_PostImportStaleFormat)
                        ?? "Проверка после импорта: устарело {0} из {1} — обновите через «Обновить» в контекстном меню",
                    staleCount, results.Count);
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Post-import stale check failed: {ex.GetType().Name}: {ex.Message} " +
                "[Action: не критично — запустите «Проверить» вручную из контекстного меню]");
        }
        finally
        {
            IsStaleCheckInProgress = false;
            StaleCheckMessage = null;
            CompleteProgress();
            NotifyCheckCommands();
        }
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
