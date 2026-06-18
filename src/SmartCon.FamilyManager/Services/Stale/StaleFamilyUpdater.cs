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
    private readonly IFamilyFinder _familyFinder;
    private readonly IFamilyVersionWriter _versionWriter;
    private readonly IClock _clock;

    public StaleFamilyUpdater(
        IFamilyLoadService loadService,
        IFamilyFileResolver fileResolver,
        IFamilyVersionStore versionStore,
        IFamilyManagerDialogService dialogService,
        IFamilyManagerAwaitableEvent awaitable,
        IRevitContext revitContext,
        IFamilyFinder familyFinder,
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
        ArgumentNullException.ThrowIfNull(familyFinder);
        ArgumentNullException.ThrowIfNull(versionWriter);
        ArgumentNullException.ThrowIfNull(clock);
#else
        if (loadService is null) throw new ArgumentNullException(nameof(loadService));
        if (fileResolver is null) throw new ArgumentNullException(nameof(fileResolver));
        if (versionStore is null) throw new ArgumentNullException(nameof(versionStore));
        if (dialogService is null) throw new ArgumentNullException(nameof(dialogService));
        if (awaitable is null) throw new ArgumentNullException(nameof(awaitable));
        if (revitContext is null) throw new ArgumentNullException(nameof(revitContext));
        if (familyFinder is null) throw new ArgumentNullException(nameof(familyFinder));
        if (versionWriter is null) throw new ArgumentNullException(nameof(versionWriter));
        if (clock is null) throw new ArgumentNullException(nameof(clock));
#endif
        _loadService = loadService;
        _fileResolver = fileResolver;
        _versionStore = versionStore;
        _dialogService = dialogService;
        _awaitable = awaitable;
        _revitContext = revitContext;
        _familyFinder = familyFinder;
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
            return new StaleBatchUpdateResult(0, 0, 0, [], []);
        }

        using var _scope = SmartConLogger.BeginScope(
            "StaleDetection",
            ("Method", nameof(UpdateBatchAsync)),
            ("Count", request.CatalogItemIds.Count));

        var total = request.CatalogItemIds.Count;
        var successIds = new List<string>();
        var failedIds = new List<string>();

        for (var i = 0; i < total; i++)
        {
            if (ct.IsCancellationRequested) break;
            var id = request.CatalogItemIds[i];

            var (ok, familyName) = await UpdateFamilyCoreAsync(id, request.OverwriteParameterValues, ct)
                .ConfigureAwait(true);
            if (ok) successIds.Add(id);
            else failedIds.Add(id);

            progress?.Report(new StaleBatchUpdateProgress(i + 1, total, familyName ?? id));
        }

        return new StaleBatchUpdateResult(
            TotalRequested: total,
            SuccessCount: successIds.Count,
            FailedCount: failedIds.Count,
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
            if (string.IsNullOrEmpty(resolved.AbsolutePath)) return (false, null);

            var loadOptions = FamilyLoadOptions.Default with
            {
                PreferredName = null,
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

            // Persist fresh marker via shared helper (also used by LoadPlace partial).
            await _versionWriter.WriteVersionMarkerAsync(
                catalogItemId,
                result.FamilyName ?? resolved.VersionLabel ?? string.Empty,
                resolved.VersionLabel,
                targetRevit,
                ct).ConfigureAwait(true);

            return (true, result.FamilyName);
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
