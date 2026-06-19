using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.Stale;

/// <summary>
/// Sequential batch updater for stale families (ADR-030, Issue #69 AC).
/// Reuses the existing <see cref="IFamilyLoadService"/> for the actual Revit
/// Load/Update, then writes a fresh <see cref="FamilyVersion"/> marker via
/// <see cref="IFamilyVersionStore"/>.
/// </summary>
internal sealed class StaleFamilyUpdater : IStaleFamilyUpdater
{
    private readonly IFamilyLoadService _loadService;
    private readonly IFamilyFileResolver _fileResolver;
    private readonly IFamilyVersionStore _versionStore;
    private readonly IFamilyManagerDialogService _dialogService;
    private readonly IFamilyManagerAwaitableEvent _awaitable;
    private readonly IRevitContext _revitContext;
    private readonly IFamilyVersionWriter _versionWriter;
    private readonly IClock _clock;

    public StaleFamilyUpdater(
        IFamilyLoadService loadService,
        IFamilyFileResolver fileResolver,
        IFamilyVersionStore versionStore,
        IFamilyManagerDialogService dialogService,
        IFamilyManagerAwaitableEvent awaitable,
        IRevitContext revitContext,
        IFamilyVersionWriter versionWriter,
        IClock clock)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(loadService);
        ArgumentNullException.ThrowIfNull(fileResolver);
        ArgumentNullException.ThrowIfNull(versionStore);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(awaitable);
        ArgumentNullException.ThrowIfNull(revitContext);
        ArgumentNullException.ThrowIfNull(versionWriter);
        ArgumentNullException.ThrowIfNull(clock);
#else
        if (loadService is null) throw new ArgumentNullException(nameof(loadService));
        if (fileResolver is null) throw new ArgumentNullException(nameof(fileResolver));
        if (versionStore is null) throw new ArgumentNullException(nameof(versionStore));
        if (dialogService is null) throw new ArgumentNullException(nameof(dialogService));
        if (awaitable is null) throw new ArgumentNullException(nameof(awaitable));
        if (revitContext is null) throw new ArgumentNullException(nameof(revitContext));
        if (versionWriter is null) throw new ArgumentNullException(nameof(versionWriter));
        if (clock is null) throw new ArgumentNullException(nameof(clock));
#endif
        _loadService = loadService;
        _fileResolver = fileResolver;
        _versionStore = versionStore;
        _dialogService = dialogService;
        _awaitable = awaitable;
        _revitContext = revitContext;
        _versionWriter = versionWriter;
        _clock = clock;
    }

    public async Task<bool> UpdateFamilyAsync(
        string catalogItemId,
        bool overwriteParameterValues,
        CancellationToken ct)
    {
        using var _scope = SmartConLogger.BeginScope(
            "StaleDetection",
            ("Method", nameof(UpdateFamilyAsync)),
            ("CatalogItemId", catalogItemId));

        var result = await UpdateFamilyCoreAsync(catalogItemId, overwriteParameterValues, ct)
            .ConfigureAwait(true);
        return result.success;
    }

    public async Task<StaleBatchUpdateResult> UpdateBatchAsync(
        StaleUpdateRequest request,
        IProgress<StaleBatchUpdateProgress>? progress = null,
        CancellationToken ct = default)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(request);
#else
        if (request is null) throw new ArgumentNullException(nameof(request));
#endif
        if (request.CatalogItemIds is null || request.CatalogItemIds.Count == 0)
        {
            return new StaleBatchUpdateResult(0, 0, 0, 0, [], []);
        }

        using var _scope = SmartConLogger.BeginScope(
            "StaleDetection",
            ("Method", nameof(UpdateBatchAsync)),
            ("Count", request.CatalogItemIds.Count));

        var total = request.CatalogItemIds.Count;
        var successIds = new List<string>();
        var failedIds = new List<string>();
        var processed = 0;

        for (var i = 0; i < total; i++)
        {
            if (ct.IsCancellationRequested) break;
            var id = request.CatalogItemIds[i];

            var (ok, familyName) = await UpdateFamilyCoreAsync(id, request.OverwriteParameterValues, ct)
                .ConfigureAwait(true);
            if (ok) successIds.Add(id);
            else failedIds.Add(id);
            processed++;

            progress?.Report(new StaleBatchUpdateProgress(processed, total, familyName ?? id));
        }

        return new StaleBatchUpdateResult(
            TotalRequested: total,
            SuccessCount: successIds.Count,
            FailedCount: failedIds.Count,
            SkippedCount: total - processed,
            SuccessCatalogItemIds: successIds,
            FailedCatalogItemIds: failedIds);
    }

    private async Task<(bool success, string? familyName)> UpdateFamilyCoreAsync(
        string catalogItemId,
        bool overwriteParameterValues,
        CancellationToken ct)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(catalogItemId);
#else
        if (catalogItemId is null) throw new ArgumentNullException(nameof(catalogItemId));
#endif
        // Note: caller (UpdateFamilyAsync or UpdateBatchAsync) already opened BeginScope.
        try
        {
            var targetRevit = ResolveTargetRevit(catalogItemId);

            var resolved = await _fileResolver
                .ResolveForLoadAsync(catalogItemId, targetRevit, ct)
                .ConfigureAwait(true);
            if (string.IsNullOrEmpty(resolved.AbsolutePath))
            {
                SmartConLogger.Warn(
                    $"UpdateFamily[{catalogItemId}]: fileResolver returned empty path. " +
                    "[Action: catalog item is not available on disk for the current Revit " +
                    "version; the family will be skipped and the next Check will mark it " +
                    "stale again]");
                return (false, null);
            }

            var loadOptions = FamilyLoadOptions.Default with
            {
                OverwriteParameterValues = overwriteParameterValues,
            };

            // IFamilyLoadService.LoadFamilyAsync is a sync wrapper (Task.FromResult) — safe to block.
            // See revit-api-best-practice: ".GetAwaiter().GetResult() directly — deadlock-free".
            var result = await _awaitable.RaiseAsync(
                _ => _loadService.LoadFamilyAsync(
                    resolved,
                    loadOptions,
                    onStatusMessage: null,
                    onSharedDecision: req => _dialogService.ShowSharedFamiliesLoadModeDialog(req),
                    ct: ct).GetAwaiter().GetResult(),
                ct).ConfigureAwait(true);

            if (!result.Success) return (false, result.FamilyName);

            // Persist fresh marker via shared helper. The family has already been
            // loaded into Revit at this point; if the marker write fails (ES
            // storage error, Revit main thread timeout) we still treat the
            // update as SUCCESS. Otherwise the snapshot would keep the entry
            // and the user would be prompted to update again on the next Check,
            // re-running LoadFamily with overwriteParameterValues=true and
            // corrupting any parameters the user edited in the meantime.
            // A failed marker write is logged at Warn so the operator can
            // investigate, but the in-Revit state is the source of truth.
            try
            {
                await _versionWriter.WriteVersionMarkerAsync(
                    catalogItemId,
                    result.FamilyName ?? resolved.VersionLabel ?? string.Empty,
                    resolved.VersionLabel,
                    targetRevit,
                    ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // OCE is a normal control flow (caller requested cancellation).
                // Re-raise so the outer catch in this method re-throws it
                // cleanly, and the caller's CancellationToken is honoured.
                // The family has already been loaded into Revit, but the
                // marker was not written, so on the next Check it will
                // appear stale again — which is the correct outcome for a
                // cancelled operation.
                throw;
            }
            catch (Exception markerEx)
            {
                SmartConLogger.Warn(
                    $"UpdateFamily[{catalogItemId}]: family was loaded into Revit but " +
                    $"ES marker write failed: {markerEx.GetType().Name}: {markerEx.Message}. " +
                    "The family is treated as updated (its in-Revit state is the " +
                    "source of truth); the snapshot will reflect this on the next " +
                    "tree rebuild. [Action: if the family re-appears as stale, check " +
                    "ES schema registration and Revit version reads]");
            }

            return (true, result.FamilyName);
        }
        catch (OperationCanceledException)
        {
            // OCE is a normal control flow (caller requested cancellation) -
            // do not treat as a failure and do not pollute the log with a Warn.
            // Re-raise so the caller's CancellationToken is honoured.
            throw;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"UpdateFamily[{catalogItemId}]: failed: {ex.Message}. " +
                "[Action: family skipped, batch continues]");
            return (false, null);
        }
    }

    private int ResolveTargetRevit(string catalogItemId)
    {
        try
        {
            if (int.TryParse(_revitContext.GetRevitVersion(), out var v)) return v;
            SmartConLogger.Warn(
                $"ResolveTargetRevit[{catalogItemId}]: Revit version is not a number. " +
                "[Action: targetRevit=0 fallback, version mismatch detection disabled for this batch]");
            return 0;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"ResolveTargetRevit[{catalogItemId}]: failed: {ex.Message}. " +
                "[Action: targetRevit=0 fallback, version mismatch detection disabled for this batch]");
            return 0;
        }
    }
}
