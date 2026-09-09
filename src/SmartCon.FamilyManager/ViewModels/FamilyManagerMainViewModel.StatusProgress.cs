using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Common;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Events;
using SmartCon.FamilyManager.Selectors;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.Services.LocalCatalog;
using SmartCon.FamilyManager.Services.Stale;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyManagerMainViewModel
{
    private void OnSystemTypePlaced(FamilyPlacementDragData data)
    {
        try
        {
            // Fired on the Revit main thread from the drop handler — hop to
            // the UI thread for the async badge re-eval (same pattern as
            // OnPlacementCompleted). Not awaited: UI refresh is opportunistic.
            _ = _dispatcher.InvokeAsync(() =>
            {
                _ = RefreshAfterSystemTypePlacedAsync(data);
            });
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"OnSystemTypePlaced dispatcher invoke failed: {ex.Message} [Action: run «Проверить» to refresh the stale badges]");
        }
    }

    /// <summary>
    /// ADR-066 follow-up: the DnD tail mirrors the load-to-project tail
    /// (LoadToProject → badge re-eval + full presence recompute). The sync
    /// behind a system type placement loads the type's routing fitting
    /// DEPENDENCIES into the project implicitly — only a full presence pass
    /// turns those loadable badges blue; the per-type badge re-eval alone
    /// covers just the placed parent type.
    /// </summary>
    private async Task RefreshAfterSystemTypePlacedAsync(FamilyPlacementDragData data)
    {
        await ReevaluateSystemItemBadgeAsync(
            data.CatalogItemId, data.TypeName, data.SystemFamilyName, data.SystemFamilyKey)
            .ConfigureAwait(true);
        await RefreshSystemTypeProjectPresenceSafeAsync().ConfigureAwait(true);
    }

    private void OnPlacementCompleted()
    {
        try
        {
            // v2.0.0 (ADR-036, M-019-003): IDispatcher.InvokeAsync returns Task.
            // We don't await here because OnPlacementCompleted is sync and the
            // caller is the placement event handler; UI refresh is opportunistic.
            _ = _dispatcher.InvokeAsync(() => { _ = LoadTreeAsync(); });
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"OnPlacementCompleted dispatcher invoke failed: {ex.Message} [Action: click Refresh to update the tree]");
        }
    }

    private void OnPlacementFailed(string errorMessage)
    {
        SmartConLogger.Warn($"{errorMessage} [Action: verify the family is loaded and the type exists, then click Refresh]");
        if (!SetStatusOnUiThread(errorMessage))
        {
            return;
        }
    }

    private void OnPlacementSucceeded(string successMessage)
    {
        if (!SetStatusOnUiThread(successMessage))
        {
            return;
        }
        SmartConLogger.Info($"{successMessage}");
    }

    private void OnPlacementStatusMessage(string statusMessage)
    {
        if (!SetStatusOnUiThread(statusMessage))
        {
            return;
        }
        SmartConLogger.Info($"{statusMessage}");
    }

    /// <summary>
    /// Defensive marshaling: these handlers are currently invoked on the Revit
    /// UI thread by <c>FamilyPlacementDropHandler</c>, but if a future refactor
    /// moves them to a background thread the unguarded <c>StatusMessage</c>
    /// setter would raise <c>PropertyChanged</c> on the wrong thread and
    /// freeze the WPF DockablePane (see <c>revit-api-best-practice</c> skill).
    /// </summary>
    private bool SetStatusOnUiThread(string message)
    {
        if (_dispatcher.CheckAccess())
        {
            StatusMessage = message;
        }
        else
        {
            _ = _dispatcher.InvokeAsync(() => StatusMessage = message);
        }
        return true;
    }

    // ── Pane bottom progress bar (background mini-tasks, #256/#259) ─────
    // Marshaled defensively like SetStatusOnUiThread — an off-UI-thread
    // PropertyChanged would freeze the WPF DockablePane.

    /// <summary>How long the completed bar stays visible before hiding.</summary>
    private const int ProgressCompletionHoldMs = 700;

    /// <summary>Generation of the current progress run. Incremented by
    /// <see cref="BeginProgress"/>; the delayed hide of
    /// <see cref="CompleteProgress"/> fires only while no newer run owns the bar.</summary>
    private int _progressRunId;

    /// <summary>Run whose bar was already hidden by the delayed reset —
    /// reports arriving after that must not re-show it (stuck-at-100% bug).</summary>
    private int _progressHiddenRunId;

    /// <summary>Shows the empty bar and returns the generation id of this
    /// run — the token checked by <see cref="IsProgressReportCurrent"/>.</summary>
    private int BeginProgress()
    {
        _progressRunId++;
        SetProgressOnUiThread(0, 1, true);
        return _progressRunId;
    }

    private void ReportProgress(double completed, double total) =>
        SetProgressOnUiThread(completed, total <= 0 ? 1 : total, true);

    /// <summary>
    /// Guard for <see cref="Progress{T}"/> callbacks: the report belongs to
    /// the CURRENT run and its bar was not hidden yet. Millisecond-fast runs
    /// (SQLite completes synchronously) finish before the dispatcher
    /// processes the posted reports — such reports are NOT garbage: they fill
    /// the bar during the completion hold. Dropped are only truly stale
    /// reports — from a superseded run or after the bar was hidden.
    /// </summary>
    private bool IsProgressReportCurrent(int runId) =>
        runId == _progressRunId && runId != _progressHiddenRunId;

    /// <summary>
    /// Ends the current run: the completed bar stays visible for a short hold
    /// (~0.7 s) and only then hides and resets — an instant hide made fast
    /// runs look like a flicker and the user never saw the completion. A
    /// newer <see cref="BeginProgress"/> cancels the pending hide (run-id
    /// guard), so back-to-back operations never lose their bar.
    /// </summary>
    private void CompleteProgress()
    {
        var runId = _progressRunId;
        SmartConLogger.Debug(
            $"CompleteProgress: run {runId} finished — holding the bar for {ProgressCompletionHoldMs}ms, then hide+reset");
        FireAndForget(async () =>
        {
            await Task.Delay(ProgressCompletionHoldMs);
            if (_progressRunId != runId)
            {
                SmartConLogger.Debug(
                    $"CompleteProgress: pending hide of run {runId} cancelled — newer run {_progressRunId} owns the bar");
                return;
            }
            ResetProgress();
            _progressHiddenRunId = runId;
            SmartConLogger.Debug($"CompleteProgress: run {runId} bar hidden and reset");
        }, nameof(CompleteProgress));
    }

    private void ResetProgress() => SetProgressOnUiThread(0, 1, false);

    private void SetProgressOnUiThread(double value, double maximum, bool visible)
    {
        if (_dispatcher.CheckAccess())
        {
            ProgressMaximum = maximum;
            ProgressValue = value;
            IsProgressVisible = visible;
        }
        else
        {
            _ = _dispatcher.InvokeAsync(() =>
            {
                ProgressMaximum = maximum;
                ProgressValue = value;
                IsProgressVisible = visible;
            });
        }
    }
}
