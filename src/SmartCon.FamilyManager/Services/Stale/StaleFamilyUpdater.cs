using Autodesk.Revit.DB;
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
    private readonly IClock _clock;
    private readonly IDispatcher _dispatcher;

    public StaleFamilyUpdater(
        IFamilyLoadService loadService,
        IFamilyFileResolver fileResolver,
        IFamilyVersionStore versionStore,
        IFamilyManagerDialogService dialogService,
        IFamilyManagerAwaitableEvent awaitable,
        IRevitContext revitContext,
        IClock clock,
        IDispatcher dispatcher)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(loadService);
        ArgumentNullException.ThrowIfNull(fileResolver);
        ArgumentNullException.ThrowIfNull(versionStore);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(awaitable);
        ArgumentNullException.ThrowIfNull(revitContext);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(dispatcher);
#else
        if (loadService is null) throw new ArgumentNullException(nameof(loadService));
        if (fileResolver is null) throw new ArgumentNullException(nameof(fileResolver));
        if (versionStore is null) throw new ArgumentNullException(nameof(versionStore));
        if (dialogService is null) throw new ArgumentNullException(nameof(dialogService));
        if (awaitable is null) throw new ArgumentNullException(nameof(awaitable));
        if (revitContext is null) throw new ArgumentNullException(nameof(revitContext));
        if (clock is null) throw new ArgumentNullException(nameof(clock));
        if (dispatcher is null) throw new ArgumentNullException(nameof(dispatcher));
#endif
        _loadService = loadService;
        _fileResolver = fileResolver;
        _versionStore = versionStore;
        _dialogService = dialogService;
        _awaitable = awaitable;
        _revitContext = revitContext;
        _clock = clock;
        _dispatcher = dispatcher;
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
        using var _scope = SmartConLogger.BeginScope(
            "StaleDetection",
            ("Method", nameof(UpdateBatchAsync)),
            ("Count", request?.CatalogItemIds?.Count ?? 0));

#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(request);
#else
        if (request is null) throw new ArgumentNullException(nameof(request));
#endif
        if (request.CatalogItemIds is null || request.CatalogItemIds.Count == 0)
        {
            return new StaleBatchUpdateResult(0, 0, 0, []);
        }

        var total = request.CatalogItemIds.Count;
        var success = 0;
        var failedIds = new List<string>();

        for (var i = 0; i < total; i++)
        {
            if (ct.IsCancellationRequested) break;
            var id = request.CatalogItemIds[i];

            var (ok, familyName) = await UpdateFamilyCoreAsync(id, request.OverwriteParameterValues, ct)
                .ConfigureAwait(true);
            if (ok) success++;
            else failedIds.Add(id);

            progress?.Report(new StaleBatchUpdateProgress(i + 1, total, familyName ?? id));
        }

        return new StaleBatchUpdateResult(total, success, failedIds.Count, failedIds);
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
            var targetRevit = 0;
            try
            {
                if (int.TryParse(_revitContext.GetRevitVersion(), out var v)) targetRevit = v;
            }
            catch
            {
            }

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
                    ct: CancellationToken.None).GetAwaiter().GetResult(),
                ct).ConfigureAwait(true);

            if (!result.Success) return (false, result.FamilyName);

            // Persist fresh marker.
            var version = new FamilyVersion(
                SchemaVersion: FamilyVersion.CurrentSchemaVersion,
                CatalogItemId: catalogItemId,
                VersionLabel: resolved.VersionLabel ?? string.Empty,
                LoadedAtUtc: _clock.UtcNow,
                SourceRevitVersion: targetRevit);

            await _awaitable.RaiseAsyncTask(_ =>
            {
                var doc = _revitContext.GetDocument();
                var family = FindFamilyByName(doc, result.FamilyName ?? resolved.VersionLabel ?? string.Empty);
                if (family is null) return Task.CompletedTask;
                _versionStore.WriteToLoadedFamily(doc, family.Id, version);
                return Task.CompletedTask;
            }, ct).ConfigureAwait(true);

            return (true, result.FamilyName);
        }
        catch (Exception ex)
        {
            using var _scope = SmartConLogger.BeginScope(
                "StaleDetection",
                ("Method", nameof(UpdateFamilyCoreAsync)),
                ("CatalogItemId", catalogItemId));
            SmartConLogger.Warn(
                $"UpdateFamily[{catalogItemId}]: failed: {ex.Message}. " +
                "[Action: family skipped, batch continues]");
            return (false, null);
        }
    }

    private static Autodesk.Revit.DB.Family? FindFamilyByName(Document doc, string familyName)
    {
        if (doc is null || string.IsNullOrEmpty(familyName)) return null;
        using var collector = new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Family));
        foreach (Autodesk.Revit.DB.Family f in collector)
        {
            if (string.Equals(f.Name, familyName, StringComparison.Ordinal)) return f;
        }
        return null;
    }
}
