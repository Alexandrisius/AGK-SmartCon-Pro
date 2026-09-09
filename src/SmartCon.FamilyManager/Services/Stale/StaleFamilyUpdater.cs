using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.Stale;

/// <summary>
/// Sequential batch updater for stale families (ADR-030, Issue #69 AC).
/// Reuses the existing <see cref="IFamilyLoadService"/> for the actual Revit
/// Load/Update, then writes a fresh <see cref="FamilyVersion"/> marker via
/// <see cref="IFamilyVersionStore"/>.
/// </summary>
internal sealed partial class StaleFamilyUpdater : IStaleFamilyUpdater
{
    private readonly IFamilyLoadService _loadService;
    private readonly IFamilyFileResolver _fileResolver;
    private readonly IFamilyVersionStore _versionStore;
    private readonly IFamilyManagerDialogService _dialogService;
    private readonly IFamilyManagerAwaitableEvent _awaitable;
    private readonly IRevitContext _revitContext;
    private readonly IFamilyVersionWriter _versionWriter;
    private readonly IClock _clock;
    private readonly ISharedNestedFamilyRepository? _nestedSharedRepository;
    private readonly IFamilyCatalogProvider? _catalog;
    private readonly IFamilyTypeRepository? _typeRepository;
    private readonly ISystemTypeSyncOrchestrator? _systemSyncOrchestrator;
    private readonly IFamilyDependencyRepository? _dependencyRepository;
    private readonly IFamilySnapshotExtractor? _snapshotExtractor;
    private readonly IFamilyContentHasher? _contentHasher;
    private readonly IAttributeValueRepository? _attributeValueRepository;
    private readonly IFamilySearchService? _familySearchService;

    public StaleFamilyUpdater(
        IFamilyLoadService loadService,
        IFamilyFileResolver fileResolver,
        IFamilyVersionStore versionStore,
        IFamilyManagerDialogService dialogService,
        IFamilyManagerAwaitableEvent awaitable,
        IRevitContext revitContext,
        IFamilyVersionWriter versionWriter,
        IClock clock,
        ISharedNestedFamilyRepository? nestedSharedRepository = null,
        IFamilyCatalogProvider? catalog = null,
        IFamilyTypeRepository? typeRepository = null,
        ISystemTypeSyncOrchestrator? systemSyncOrchestrator = null,
        IFamilyDependencyRepository? dependencyRepository = null,
        IFamilySnapshotExtractor? snapshotExtractor = null,
        IFamilyContentHasher? contentHasher = null,
        IAttributeValueRepository? attributeValueRepository = null,
        IFamilySearchService? familySearchService = null)
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
        _nestedSharedRepository = nestedSharedRepository;
        _catalog = catalog;
        _typeRepository = typeRepository;
        _systemSyncOrchestrator = systemSyncOrchestrator;
        _dependencyRepository = dependencyRepository;
        _snapshotExtractor = snapshotExtractor;
        _contentHasher = contentHasher;
        _attributeValueRepository = attributeValueRepository;
        _familySearchService = familySearchService;
    }

    public async Task<StaleFamilyUpdateResult> UpdateFamilyAsync(
        string catalogItemId,
        bool overwriteParameterValues,
        string? fromVersionLabel,
        CancellationToken ct)
    {
        using var _scope = SmartConLogger.BeginScope(
            "StaleDetection",
            ("Method", nameof(UpdateFamilyAsync)),
            ("CatalogItemId", catalogItemId));

        return await UpdateFamilyCoreAsync(catalogItemId, overwriteParameterValues, fromVersionLabel, ct)
            .ConfigureAwait(true);
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

            // Batch passes no fromVersionLabel — the #222 per-type change
            // report is a single-update UX feature; the batch result stays
            // aggregate (ids only).
            var updateResult = await UpdateFamilyCoreAsync(id, request.OverwriteParameterValues, null, ct)
                .ConfigureAwait(true);
            if (updateResult.Success) successIds.Add(id);
            else failedIds.Add(id);
            processed++;

            progress?.Report(new StaleBatchUpdateProgress(processed, total, updateResult.FamilyName ?? id));
        }

        return new StaleBatchUpdateResult(
            TotalRequested: total,
            SuccessCount: successIds.Count,
            FailedCount: failedIds.Count,
            SkippedCount: total - processed,
            SuccessCatalogItemIds: successIds,
            FailedCatalogItemIds: failedIds);
    }

    private async Task<StaleFamilyUpdateResult> UpdateSystemFamilyCoreAsync(
        FamilyCatalogItem item,
        int targetRevit,
        CancellationToken ct)
    {
        var descriptors = await _typeRepository!
            .GetTypesForItemAsync(item.Id, ct)
            .ConfigureAwait(true);
        // #183/#190: full identity (familyKey, family, name) per type —
        // dropping FamilyKey breaks the locale-invariant lookup in the
        // reference mini-project (ADR-064).
        var types = descriptors
            .Select(d => new SystemTypeRef(d.Name, d.FamilyName, d.FamilyKey))
            .ToList();
        if (types.Count == 0)
        {
            SmartConLogger.Warn(
                $"UpdateSystemFamily[{item.Id}]: no types in the catalog for '{item.Name}'. " +
                "[Action: reimport the mini-project to rebuild the type list]");
            return StaleFamilyUpdateResult.Failure(item.Name);
        }

        var result = await _awaitable.RaiseAsync(
            _ => _systemSyncOrchestrator!.SyncTypes(
                _revitContext.GetDocument(), item.Id, types, targetRevit),
            ct).ConfigureAwait(true);

        if (!result.AllSucceeded)
        {
            var failed = result.TypeResults
                .Where(r => !r.IsSuccess)
                .Select(r => r.TypeName)
                .ToList();
            SmartConLogger.Warn(
                $"UpdateSystemFamily[{item.Id}]: {result.SuccessCount}/{result.TypeResults.Count} " +
                $"types synchronized; failed: [{string.Join(", ", failed)}]. " +
                "[Action: the item stays stale — check the log for per-type errors and retry]");
            return StaleFamilyUpdateResult.Failure(item.Name);
        }

        return StaleFamilyUpdateResult.SuccessWithoutReport(item.Name, contentAlreadyCurrent: false);
    }

    /// <summary>
    /// Persists the fresh ES marker (and nested-dependency markers) after a
    /// successful update. Best-effort: if the marker write fails (ES
    /// storage error, Revit main thread timeout) we still treat the update
    /// as SUCCESS — when a reload did happen, re-prompting would re-run
    /// LoadFamily with overwriteParameterValues=true and corrupt any
    /// parameters the user edited in the meantime; when the content was
    /// verified without a reload, the in-Revit state is equally the source
    /// of truth. A failed write is logged at Warn so the operator can
    /// investigate.
    /// </summary>
    private async Task WriteMarkerBestEffortAsync(
        string catalogItemId,
        FamilyResolvedFile resolved,
        string? familyName,
        int targetRevit,
        CancellationToken ct)
    {
        var markerName = familyName
            ?? System.IO.Path.GetFileNameWithoutExtension(resolved.AbsolutePath)
            ?? resolved.VersionLabel
            ?? string.Empty;
        try
        {
            await _versionWriter.WriteVersionMarkerAsync(
                catalogItemId,
                markerName,
                resolved.VersionLabel,
                targetRevit,
                ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // OCE is a normal control flow (caller requested cancellation).
            // The family has already been loaded into Revit, but the marker
            // was not written, so on the next Check it will appear stale
            // again — the correct outcome for a cancelled operation.
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

        // E2 (#209): the reload refreshed the embedded nested copies in
        // the project — mark them with their embedded versions so they
        // stay in the stale cycle. Non-fatal by design (same rationale
        // as the parent marker above).
        if (_dependencyRepository is not null && _catalog is not null)
        {
            try
            {
                await NestedDependencyMarkerWriter.WriteMarkersAsync(
                    _dependencyRepository,
                    _catalog,
                    _versionWriter,
                    catalogItemId,
                    targetRevit,
                    ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception nestedEx)
            {
                SmartConLogger.Warn(
                    $"UpdateFamily[{catalogItemId}]: nested dependency markers failed: " +
                    $"{nestedEx.GetType().Name}: {nestedEx.Message}. " +
                    "[Action: вложенные семейства в проекте останутся без маркеров — Проверить покажет их stale только после явной загрузки]");
            }
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
