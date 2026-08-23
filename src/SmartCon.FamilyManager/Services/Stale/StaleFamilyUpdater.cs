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

    private async Task<StaleFamilyUpdateResult> UpdateFamilyCoreAsync(
        string catalogItemId,
        bool overwriteParameterValues,
        string? fromVersionLabel,
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

            // Issue #104: system catalog items are synchronized from the
            // mini-project (parameter/structure/routing data written into the
            // existing project types), never re-loaded like .rfa families.
            // The ES marker is written by the synchronizer inside its
            // per-type transaction. overwriteParameterValues is not
            // applicable — the catalog reference always overwrites.
            if (_catalog is not null && _typeRepository is not null && _systemSyncOrchestrator is not null)
            {
                var item = await _catalog.GetItemAsync(catalogItemId, ct).ConfigureAwait(true);
                if (item is not null && item.FamilySource == "system")
                {
                    return await UpdateSystemFamilyCoreAsync(item, targetRevit, ct).ConfigureAwait(true);
                }
            }

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
                return StaleFamilyUpdateResult.Failure(null);
            }

            // Pre-resolve shared-nested names BEFORE entering the ExternalEvent
            // callback. The callback below blocks the Revit main thread with
            // .GetAwaiter().GetResult(), which is only safe when every await
            // inside completes synchronously. Resolving the SQLite lookup here
            // (true async caller context) removes the only asynchronous gap
            // from ReloadFamilyPreservingLoadedTypesAsync — a latent deadlock
            // if Microsoft.Data.Sqlite ever yields asynchronously.
            IReadOnlyList<string>? nestedNames = null;
            if (_nestedSharedRepository is not null)
            {
                try
                {
                    nestedNames = await _nestedSharedRepository
                        .GetNamesForCurrentVersionAsync(catalogItemId, ct)
                        .ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    SmartConLogger.Warn(
                        $"UpdateFamily[{catalogItemId}]: failed to pre-resolve nested names: " +
                        $"{ex.GetType().Name}: {ex.Message}. " +
                        "[Action: continuing without fallback names — dialog may show placeholder in Revit 2023/2024.2]");
                }
            }

            var canVerify = _snapshotExtractor is not null && _contentHasher is not null;

            // #209 optimization: when the load service is source-aware, the
            // whole FAMILY-document cycle (pre-verify → poke+merge → failed-
            // reload arbitration → post-verify) runs in ONE Revit-thread
            // pass sharing a single OpenDocumentFile of the resolved file
            // (the legacy path below opens it up to 3 times). Returns
            // NotApplicable only for a project document — the legacy path
            // below then runs unchanged.
            if (canVerify && _loadService is IFamilyLoadServiceSourceAware sourceAware)
            {
                var orchestrated = await _awaitable.RaiseAsync(
                    _ => UpdateInFamilyDocumentOnRevitThread(
                        catalogItemId, resolved, sourceAware, overwriteParameterValues),
                    ct).ConfigureAwait(true);
                if (orchestrated.Kind != FamilyDocumentUpdateKind.NotApplicable)
                {
                    if (orchestrated.Kind == FamilyDocumentUpdateKind.Failed)
                    {
                        return StaleFamilyUpdateResult.Failure(orchestrated.FamilyName);
                    }
                    await WriteMarkerBestEffortAsync(catalogItemId, resolved, orchestrated.FamilyName, targetRevit, ct)
                        .ConfigureAwait(true);
                    var alreadyCurrent = orchestrated.Kind is FamilyDocumentUpdateKind.PreVerified
                        or FamilyDocumentUpdateKind.Arbitrated;
                    return StaleFamilyUpdateResult.SuccessWithoutReport(orchestrated.FamilyName, alreadyCurrent);
                }
            }

            // #209 round-3 + #222: pre-verify BEFORE any reload, uniformly in
            // family documents and PROJECTS (the content proof applies to a
            // loaded project family the same way — EditFamily extraction,
            // see StaleCheckContentFallbackTests). Case covered: the previous
            // batch already reloaded the family (a retry's LoadFamily then
            // returns false for "unchanged" and used to be reported as a
            // hard failure). If the embedded content already equals the
            // catalog target, the update is a no-op success — write the
            // marker without touching Revit.
            // (Legacy path — used when the load service is not source-aware.)
            if (canVerify
                && StaleUpdateVerificationPolicy.ShouldSkipReload(
                    await VerifyEmbeddedMatchesResolvedFileAsync(
                        catalogItemId, resolved, null, ct, isPostReload: false)
                    .ConfigureAwait(true)))
            {
                SmartConLogger.Info(
                    $"UpdateFamily[{catalogItemId}]: embedded content already matches catalog " +
                    $"{resolved.VersionLabel} — reload skipped, writing marker only");
                var embeddedName = System.IO.Path.GetFileNameWithoutExtension(resolved.AbsolutePath);
                await WriteMarkerBestEffortAsync(catalogItemId, resolved, embeddedName, targetRevit, ct)
                    .ConfigureAwait(true);
                return StaleFamilyUpdateResult.SuccessWithoutReport(embeddedName, contentAlreadyCurrent: true);
            }

            // Issue #101: Stale Update must reload the family while preserving
            // the set of types currently loaded in the project. A plain
            // LoadFamily pulls in EVERY type defined in the .rfa (could be 50),
            // even if the user originally loaded only one via LoadFamilySymbol.
            // ReloadFamilyPreservingLoadedTypesAsync snapshots the loaded
            // symbols first and calls LoadFamilySymbol per type. When the
            // family is not loaded yet (no symbols to preserve) it falls back
            // to a full LoadFamilyAsync internally.
            //
            // Issue #239: the per-symbol merge overwrites parameter values
            // only for the FIRST reloaded symbol (probe-proven). Build the
            // overwrite post-pass plan from the catalog — per-type parameter
            // values of the TARGET version, read from SQLite (zero extra file
            // opens) — and pass it down: the load service applies it to every
            // loaded symbol inside the reload's TransactionGroup.
            IReadOnlyList<TypeParameterOverwriteOperation>? overwriteOperations = null;
            if (overwriteParameterValues
                && resolved.VersionId is not null
                && _attributeValueRepository is not null
                && _typeRepository is not null)
            {
                try
                {
                    var targetValues = await _attributeValueRepository
                        .GetValuesForItemAsync(catalogItemId, resolved.VersionId, ct)
                        .ConfigureAwait(true);
                    var targetTypes = await _typeRepository
                        .GetTypesForItemVersionAsync(catalogItemId, resolved.VersionId, ct)
                        .ConfigureAwait(true);
                    overwriteOperations = TypeParameterOverwritePlanner.Plan(
                        targetValues, ToTypeNameMap(targetTypes));
                    SmartConLogger.Info(
                        $"UpdateFamily[{catalogItemId}]: overwrite post-pass plan — " +
                        $"{overwriteOperations.Count} operation(s) from {targetValues.Count} catalog value(s)");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    SmartConLogger.Warn(
                        $"UpdateFamily[{catalogItemId}]: failed to build the overwrite post-pass plan: " +
                        $"{ex.GetType().Name}: {ex.Message}. " +
                        "[Action: update falls back to Revit's merge only — multi-type families may fail " +
                        "post-verify and stay stale; check the catalog DB availability]");
                    overwriteOperations = null;
                }
            }

            var result = await _awaitable.RaiseAsync(
                _ => _loadService.ReloadFamilyPreservingLoadedTypesAsync(
                    resolved,
                    overwriteParameterValues,
                    onStatusMessage: null,
                    onSharedDecision: req => _dialogService.ShowSharedFamiliesLoadModeDialog(req),
                    nestedSharedNames: nestedNames,
                    overwriteOperations: overwriteOperations,
                    ct: ct).GetAwaiter().GetResult(),
                ct).ConfigureAwait(true);

            if (!result.Success)
            {
                // LoadFamily also reports failure when the file content is
                // UNCHANGED versus what is loaded (callbacks never fire) —
                // indistinguishable from a real rejection at this point, so
                // let the content verification arbitrate (uniformly in family
                // documents and projects, #222). A rejected reload whose
                // content genuinely differs must never be masked as success.
                if (canVerify
                    && StaleUpdateVerificationPolicy.ShouldAcceptFailedReload(
                        await VerifyEmbeddedMatchesResolvedFileAsync(
                            catalogItemId, resolved, result.FamilyName, ct)
                        .ConfigureAwait(true)))
                {
                    SmartConLogger.Info(
                        $"UpdateFamily[{catalogItemId}]: reload reported failure but embedded content " +
                        $"matches catalog {resolved.VersionLabel} — treating as already up-to-date");
                    await WriteMarkerBestEffortAsync(catalogItemId, resolved, result.FamilyName, targetRevit, ct)
                        .ConfigureAwait(true);
                    return StaleFamilyUpdateResult.SuccessWithoutReport(result.FamilyName, contentAlreadyCurrent: true);
                }
                return StaleFamilyUpdateResult.Failure(result.FamilyName);
            }

            // #209 (manual-test bug) + #222: a reload can be a silent no-op
            // while markers claim the new version (family document: Revit's
            // changedness wall; project: LoadFamilySymbol skipped types whose
            // definition did not change while OTHERS failed to land). Verify
            // the embedded content hash equals the catalog target version
            // BEFORE writing any marker — uniformly in family documents and
            // projects. A failed verification = failed update (no marker,
            // the family stays stale and the next Check offers it again).
            if (canVerify)
            {
                var verified = await VerifyEmbeddedMatchesResolvedFileAsync(
                        catalogItemId, resolved, result.FamilyName, ct)
                    .ConfigureAwait(true);
                if (StaleUpdateVerificationPolicy.ShouldFailSuccessfulReload(verified))
                {
                    return StaleFamilyUpdateResult.Failure(result.FamilyName);
                }
            }

            await WriteMarkerBestEffortAsync(catalogItemId, resolved, result.FamilyName, targetRevit, ct)
                .ConfigureAwait(true);

            // #222: per-type change report for the single-update UX («успешно»
            // must not read as «мой параметр обновился» when the changes
            // landed in types the project does not have loaded).
            var report = await BuildTypeChangeReportAsync(
                catalogItemId, result.FamilyName, resolved, fromVersionLabel, targetRevit, ct)
                .ConfigureAwait(true);
            return new StaleFamilyUpdateResult(
                true, result.FamilyName, report.Loaded, report.NotLoaded, report.Available, ContentAlreadyCurrent: false);
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
            return StaleFamilyUpdateResult.Failure(null);
        }
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
    /// #209 round-4 + #222: content verification for BOTH the family-document
    /// and the project context. Compares the unified FHV10 content hash
    /// (<see cref="IFamilyContentHasher.ComputeForLoadable"/>)
    /// of the family embedded/loaded in the active document against
    /// the same hash of the resolved source .rfa — both extracted in
    /// the same live session, so the comparison is context-consistent
    /// (probe 2026-08-12: the embedded EditFamily document keeps the
    /// authored state byte-for-byte even under a host drive — the merge
    /// transfers everything except parameter groups, which FHV10 no longer
    /// hashes at all; the check side applies the same proof to project-loaded
    /// families, see StaleCheckContentFallbackTests). The file hash is
    /// restricted to the embedded type set ONLY in a project (type-set rule,
    /// stress test 2026-08-14) — a partially loaded multi-type family
    /// otherwise mismatches the TYPES section structurally; in a family
    /// document the comparison is full-vs-full because the merge transfers
    /// every type.
    /// Tri-state: <c>true</c> — embedded matches the file; <c>false</c> —
    /// differs (the reload did not land and the update MUST fail before
    /// any marker is written); <c>null</c> — indeterminate (file unreadable;
    /// family not found in the document; open in the editor; transient
    /// extraction failure). Callers must distinguish via
    /// <see cref="StaleUpdateVerificationPolicy"/>: only an explicit
    /// <c>true</c> may skip/arbitrate a reload, only an explicit
    /// <c>false</c> fails an otherwise successful one.
    /// </summary>
    private async Task<bool?> VerifyEmbeddedMatchesResolvedFileAsync(
        string catalogItemId,
        FamilyResolvedFile resolved,
        string? familyName,
        CancellationToken ct,
        bool isPostReload = true)
    {
        var name = !string.IsNullOrEmpty(familyName)
            ? familyName!
            : System.IO.Path.GetFileNameWithoutExtension(resolved.AbsolutePath);

        var (embeddedHash, fileHash) = await _awaitable.RaiseAsync<(string?, string?)>(app =>
        {
            // TryGetDocument (#219): zero-document state is a quiet
            // "indeterminate", never an NRE.
            var doc = _revitContext.TryGetDocument();
            if (doc is null)
            {
                return (null, null);
            }

            var (embedded, typeNames) = ComputeEmbeddedVerificationHashOnRevitThread(doc, name, catalogItemId);
            if (embedded is null)
            {
                return (null, null);
            }

            // Type-set rule (stress test 2026-08-14, validator round): the
            // file hash is restricted to the embedded type set ONLY in a
            // project (preserve-types reload keeps the loaded subset —
            // an unrestricted comparison never matches). In a family
            // document the full merge transfers every type, so the
            // comparison stays full-vs-full: a type-adding version bump
            // must mismatch pre-reload, otherwise the update would be
            // falsely skipped and the new type would never land.
            var file = EmbeddedContentVerifier.ComputeFileHash(
                doc, resolved.AbsolutePath, _snapshotExtractor!, _contentHasher!,
                $"UpdateFamily[{catalogItemId}]",
                restrictToTypeNames: doc.IsFamilyDocument ? null : typeNames);
            if (file is null)
            {
                return (null, null);
            }

            // Post-reload mismatch diagnostics (#239 follow-up, manual test
            // 2026-08-23): a failed post-verify must name the differing
            // content, not just two hash prefixes — log the first differing
            // canonical tokens with their section context. Failure-only:
            // costs one extra EditFamily + file open per failed update.
            if (isPostReload && !string.Equals(embedded, file, StringComparison.OrdinalIgnoreCase))
            {
                LogVerificationCanonicalDiff(doc, name, resolved.AbsolutePath, typeNames, catalogItemId);
            }

            return (embedded, file);
        }, ct).ConfigureAwait(true);

        if (embeddedHash is null || fileHash is null)
        {
            return null;
        }

        if (string.Equals(embeddedHash, fileHash, StringComparison.OrdinalIgnoreCase))
        {
            SmartConLogger.Info(
                $"UpdateFamily[{catalogItemId}]: verified — embedded '{name}' matches the resolved file ({resolved.VersionLabel})");
            return true;
        }

        var actualShort = embeddedHash.Length > 8 ? embeddedHash[..8] : embeddedHash;
        var expectedShort = fileHash.Length > 8 ? fileHash[..8] : fileHash;
        if (!isPostReload)
        {
            // Pre-verify mismatch is the NORMAL "reload needed" branch —
            // not a failure. The scary Warn is reserved for real
            // post-reload failures (manual test 2026-08-11: the pre-verify
            // Warn read as "the family stays stale" right before the
            // reload succeeded and the marker was written).
            SmartConLogger.Info(
                $"UpdateFamily[{catalogItemId}]: pre-verify — embedded '{name}' differs from the resolved file " +
                $"{resolved.VersionLabel} (embedded {actualShort}… ≠ file {expectedShort}…) — reload will run");
            return false;
        }

        SmartConLogger.Warn(
            $"UpdateFamily[{catalogItemId}]: POST-RELOAD VERIFICATION FAILED for '{name}' — embedded content " +
            $"does not match the resolved file {resolved.VersionLabel} (embedded hash {actualShort}… ≠ file {expectedShort}…). " +
            "No marker is written; the family stays stale. " +
            "[Action: обновление не заменило дефиницию — откройте родительское " +
            "семейство в редакторе, удалите проблемное вложенное и загрузите его заново из каталога (привязки " +
            "придётся восстановить), либо пересоберите родителя; затем повторите «Проверить»]");
        return false;
    }

    /// <summary>
    /// Verification-grade hash of a family nested inside an open document —
    /// delegates to <see cref="EmbeddedContentVerifier"/> (shared with the
    /// stale-check content fallback). Returns the hash AND the embedded
    /// type-name set: the file hash is restricted to the same set ONLY in a
    /// project (type-set rule — a partially loaded family otherwise never
    /// verifies); the family-document orchestrated path hashes the file
    /// full-vs-full and ignores the names.
    /// </summary>
    private (string? Hash, IReadOnlyList<string> TypeNames) ComputeEmbeddedVerificationHashOnRevitThread(
        Document doc, string familyName, string catalogItemId)
    {
        return EmbeddedContentVerifier.ComputeEmbeddedHash(
            doc, familyName, _snapshotExtractor!, _contentHasher!, $"UpdateFamily[{catalogItemId}]");
    }

    /// <summary>
    /// Post-verify failure diagnostics (#239 follow-up, manual test
    /// 2026-08-23): recomputes the embedded and file snapshots (same
    /// restriction as the verification) and logs the first differing
    /// canonical tokens with the nearest section marker (PARAMS/TYPES/GEOM/
    /// CONN/LOOKUP/…), so the operator sees WHICH content diverged instead
    /// of two opaque hash prefixes. Revit thread, failure-path only — every
    /// step is individually guarded (diagnostics must never break the flow).
    /// </summary>
    private void LogVerificationCanonicalDiff(
        Document doc, string familyName, string filePath, IReadOnlyList<string> typeNames, string catalogItemId)
    {
        try
        {
            string? embeddedCanonical = null;
            string? fileCanonical = null;

            var nested = new FilteredElementCollector(doc)
                .OfClass(typeof(Autodesk.Revit.DB.Family))
                .Cast<Autodesk.Revit.DB.Family>()
                .FirstOrDefault(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase));
            if (nested is not null)
            {
                Document? copy = null;
                try
                {
                    copy = doc.EditFamily(nested);
                    // #240: same alignment as the verification itself —
                    // otherwise an honest mismatch is reported together
                    // with phantom GEOM diffs from the current-type skew.
                    EmbeddedContentVerifier.AlignCurrentTypeForVerification(
                        copy, preferredTypeNames: null, $"UpdateFamily[{catalogItemId}]");
                    embeddedCanonical = _contentHasher!
                        .BuildLoadableCanonicalStringForDiagnostics(
                            _snapshotExtractor!.ExtractFromFamilyDocument(copy));
                }
                finally
                {
                    try { copy?.Close(false); } catch { }
                }
            }

            Document? fileDoc = null;
            try
            {
                fileDoc = doc.Application.OpenDocumentFile(filePath);
                EmbeddedContentVerifier.AlignCurrentTypeForVerification(
                    fileDoc,
                    preferredTypeNames: doc.IsFamilyDocument ? null : typeNames,
                    $"UpdateFamily[{catalogItemId}]");
                var fileSnapshot = _snapshotExtractor!.ExtractFromFamilyDocument(fileDoc);
                if (!doc.IsFamilyDocument)
                {
                    var allowed = new HashSet<string>(typeNames, StringComparer.OrdinalIgnoreCase);
                    fileSnapshot = fileSnapshot with
                    {
                        Types = fileSnapshot.Types.Where(t => allowed.Contains(t.Name)).ToList(),
                    };
                }

                fileCanonical = _contentHasher!.BuildLoadableCanonicalStringForDiagnostics(fileSnapshot);
            }
            finally
            {
                try { fileDoc?.Close(false); } catch { }
            }

            if (embeddedCanonical is null || fileCanonical is null)
            {
                SmartConLogger.Info(
                    $"UpdateFamily[{catalogItemId}]: VERIFY-DIFF unavailable (embedded={embeddedCanonical is not null}, " +
                    $"file={fileCanonical is not null})");
                return;
            }

            var e = embeddedCanonical.Split('|');
            var f = fileCanonical.Split('|');
            var shown = 0;
            var max = Math.Max(e.Length, f.Length);
            for (var i = 0; i < max && shown < 8; i++)
            {
                var et = i < e.Length ? e[i] : "<end>";
                var ft = i < f.Length ? f[i] : "<end>";
                if (et == ft) continue;

                shown++;
                var section = FindSectionMarker(e, i);
                SmartConLogger.Info(
                    $"UpdateFamily[{catalogItemId}]: VERIFY-DIFF[{shown}] section≈{section} token#{i}: " +
                    $"embedded=[{Truncate(et)}] file=[{Truncate(ft)}]");
            }

            SmartConLogger.Info(
                $"UpdateFamily[{catalogItemId}]: VERIFY-DIFF summary: tokens embedded={e.Length} file={f.Length}, " +
                $"first {shown} difference(s) shown");
        }
        catch (Exception ex)
        {
            SmartConLogger.Info(
                $"UpdateFamily[{catalogItemId}]: VERIFY-DIFF failed (diagnostics only): {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static readonly string[] SectionMarkers =
    [
        "PARAMS", "TYPES", "PHANTOM", "GEOM", "GEOM2D", "NESTED", "NONSHARED",
        "NESTEDHASH", "FACTS", "FLAGS", "CONN", "LOOKUP", "STRUCT", "ROUTING",
    ];

    private static string FindSectionMarker(string[] tokens, int index)
    {
        for (var i = index; i >= 0; i--)
        {
            if (Array.IndexOf(SectionMarkers, tokens[i]) >= 0)
                return tokens[i];
        }

        return "<prefix>";
    }

    private static string Truncate(string token)
    {
        return token.Length > 60 ? token[..60] + "…" : token;
    }

    /// <summary>
    /// #222: per-type change report for the single-update UX — which catalog
    /// types actually changed values between the version that WAS loaded
    /// (<paramref name="fromVersionLabel"/>, from the stale snapshot / ES
    /// marker) and the version updated TO, split by the set of types
    /// currently loaded in the project. <c>Available == false</c> when the
    /// diff cannot be computed honestly (unknown from-version, missing
    /// extraction rows for either version, optional dependency absent) —
    /// the caller then shows the plain success message.
    /// </summary>
    private async Task<(IReadOnlyList<string> Loaded, IReadOnlyList<string> NotLoaded, bool Available)>
        BuildTypeChangeReportAsync(
            string catalogItemId,
            string? familyName,
            FamilyResolvedFile resolved,
            string? fromVersionLabel,
            int targetRevit,
            CancellationToken ct)
    {
        if (fromVersionLabel is null
            || resolved.VersionId is null
            || _catalog is null
            || _typeRepository is null
            || _attributeValueRepository is null)
        {
            return ([], [], false);
        }

        if (string.Equals(fromVersionLabel, resolved.VersionLabel, StringComparison.OrdinalIgnoreCase))
        {
            // Updating to the same version that was loaded — an honest,
            // empty diff ("type values unchanged"), not a missing report.
            return ([], [], true);
        }

        FamilyCatalogVersion? fromVersion;
        IReadOnlyList<ExtractedAttributeValue> fromValues;
        IReadOnlyList<ExtractedAttributeValue> toValues;
        IReadOnlyList<FamilyTypeDescriptor> fromTypes;
        IReadOnlyList<FamilyTypeDescriptor> toTypes;
        try
        {
            fromVersion = await _catalog
                .GetVersionByLabelAsync(catalogItemId, fromVersionLabel, targetRevit, ct)
                .ConfigureAwait(true);
            if (fromVersion is null)
            {
                SmartConLogger.Info(
                    $"UpdateFamily[{catalogItemId}]: type-change report unavailable — " +
                    $"the previously loaded version '{fromVersionLabel}' is not in the catalog");
                return ([], [], false);
            }

            fromValues = await _attributeValueRepository
                .GetValuesForItemAsync(catalogItemId, fromVersion.Id, ct)
                .ConfigureAwait(true);
            toValues = await _attributeValueRepository
                .GetValuesForItemAsync(catalogItemId, resolved.VersionId, ct)
                .ConfigureAwait(true);
            fromTypes = await _typeRepository
                .GetTypesForItemVersionAsync(catalogItemId, fromVersion.Id, ct)
                .ConfigureAwait(true);
            toTypes = await _typeRepository
                .GetTypesForItemVersionAsync(catalogItemId, resolved.VersionId, ct)
                .ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SmartConLogger.Warn(
                $"UpdateFamily[{catalogItemId}]: type-change report failed: {ex.GetType().Name}: {ex.Message} " +
                "[Action: отчёт об изменённых типах пропущен — сама семья обновлена корректно; проверьте доступность БД каталога]");
            return ([], [], false);
        }

        if (fromValues.Count == 0 || toValues.Count == 0)
        {
            SmartConLogger.Info(
                $"UpdateFamily[{catalogItemId}]: type-change report unavailable — " +
                "extracted attribute values are missing for one of the versions " +
                "(re-run the attribute extraction for the catalog)");
            return ([], [], false);
        }

        var changed = CatalogVersionTypeDiffLogic.ComputeChangedTypeNames(
            fromValues, ToTypeNameMap(fromTypes), toValues, ToTypeNameMap(toTypes));

        IReadOnlyList<string> loadedTypeNames = [];
        if (_familySearchService is not null && !string.IsNullOrEmpty(familyName))
        {
            loadedTypeNames = await _awaitable.RaiseAsync(
                _ => _familySearchService.GetFamilyTypeNames(familyName!),
                ct).ConfigureAwait(true);
        }

        var loadedSet = new HashSet<string>(loadedTypeNames, StringComparer.OrdinalIgnoreCase);
        var loaded = changed.Where(t => loadedSet.Contains(t)).ToList();
        var notLoaded = changed.Where(t => !loadedSet.Contains(t)).ToList();
        SmartConLogger.Info(
            $"UpdateFamily[{catalogItemId}]: type-change report — {changed.Count} changed type(s) " +
            $"({loaded.Count} loaded, {notLoaded.Count} not loaded) between " +
            $"{fromVersionLabel} and {resolved.VersionLabel}");
        return (loaded, notLoaded, true);
    }

    private static Dictionary<string, string> ToTypeNameMap(IReadOnlyList<FamilyTypeDescriptor> types)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in types)
        {
            if (!map.ContainsKey(type.Id))
            {
                map[type.Id] = type.Name;
            }
        }
        return map;
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

    private enum FamilyDocumentUpdateKind
    {
        NotApplicable,
        PreVerified,
        Reloaded,
        Arbitrated,
        Failed,
    }

    /// <summary>
    /// Single-open orchestration of the FAMILY-document update cycle, running
    /// entirely on the Revit thread (inside one RaiseAsync): compute the
    /// embedded hash, open the resolved file ONCE, pre-verify, hand the open
    /// document to the source-aware load service for the poke+merge reload
    /// (borrowed — this method closes it in the finally), then arbitrate a
    /// failed reload / post-verify a successful one against the CACHED file
    /// hash (the file on disk never changes during the cycle — the poke is
    /// net-zero in-memory and the doc-to-doc merge does not touch the
    /// source). Semantics mirror the legacy
    /// <see cref="VerifyEmbeddedMatchesResolvedFileAsync"/> flow exactly;
    /// <see cref="FamilyDocumentUpdateKind.NotApplicable"/> means "fall back
    /// to the legacy path" (project document).
    /// </summary>
    private (FamilyDocumentUpdateKind Kind, string? FamilyName) UpdateInFamilyDocumentOnRevitThread(
        string catalogItemId,
        FamilyResolvedFile resolved,
        IFamilyLoadServiceSourceAware sourceAware,
        bool overwriteParameterValues)
    {
        var doc = _revitContext.GetDocument();
        if (doc is null || !doc.IsFamilyDocument)
        {
            return (FamilyDocumentUpdateKind.NotApplicable, null);
        }

        var name = System.IO.Path.GetFileNameWithoutExtension(resolved.AbsolutePath);
        var resolvedFullPath = System.IO.Path.GetFullPath(resolved.AbsolutePath);

        var (embeddedHash, _) = ComputeEmbeddedVerificationHashOnRevitThread(doc, name, catalogItemId);

        // Same C1 guard as the legacy verify: opening an ALREADY-OPEN file
        // would return the user's live document.
        var alreadyOpen = doc.Application.Documents
            .Cast<Document>()
            .Any(d => !string.IsNullOrEmpty(d.PathName)
                && string.Equals(
                    System.IO.Path.GetFullPath(d.PathName), resolvedFullPath, StringComparison.OrdinalIgnoreCase));
        if (alreadyOpen)
        {
            // Same failure the legacy path produces (the load service's own
            // guard) — reported here directly to avoid duplicate Warns and a
            // doomed reload attempt.
            SmartConLogger.Warn(
                $"UpdateFamily[{catalogItemId}]: the resolved file is open in the editor — " +
                "its on-disk content cannot be trusted for verification " +
                "[Action: закройте файл версии в редакторе (сохранив или отменив правки) и повторите «Обновить»]");
            return (FamilyDocumentUpdateKind.Failed, name);
        }

        string? fileHash = null;
        Document? fileDoc = null;

        // Family-document context: FULL file hash (validator finding
        // 2026-08-14) — the poke + doc-to-doc merge transfers the whole
        // type set, so a type-adding/removing version bump must mismatch
        // pre-reload (else the update is falsely skipped and the new type
        // never lands) and match post-reload (else a landed reload is
        // falsely failed). The type-set restriction is a PROJECT-context
        // rule only — see the legacy verify path.
        string? ComputeFullFileHash(Document openFileDoc)
        {
            // #240: align BEFORE the first extraction — the GEOM/CONN
            // sections are evaluated at the current type; the embedded
            // side is aligned by ComputeEmbeddedHash to the same
            // deterministic target (first Ordinal own type — family-doc
            // comparisons are full-vs-full, so the type sets are equal).
            EmbeddedContentVerifier.AlignCurrentTypeForVerification(
                openFileDoc, preferredTypeNames: null, $"UpdateFamily[{catalogItemId}]");
            var snap = _snapshotExtractor!.ExtractFromFamilyDocument(openFileDoc);
            return _contentHasher!.ComputeForLoadable(snap)?.HexString;
        }

        try
        {
            fileDoc = doc.Application.OpenDocumentFile(resolved.AbsolutePath);
            fileHash = ComputeFullFileHash(fileDoc);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"UpdateFamily[{catalogItemId}]: could not extract the resolved file for verification: " +
                $"{ex.GetType().Name}: {ex.Message} " +
                "[Action: верификация пропущена — проверьте, что файл версии доступен на диске]");
        }

        try
        {
            var canCompare = embeddedHash is not null && fileHash is not null;
            if (canCompare && string.Equals(embeddedHash, fileHash, StringComparison.OrdinalIgnoreCase))
            {
                SmartConLogger.Info(
                    $"UpdateFamily[{catalogItemId}]: verified — embedded '{name}' matches the resolved file ({resolved.VersionLabel})");
                SmartConLogger.Info(
                    $"UpdateFamily[{catalogItemId}]: embedded content already matches catalog " +
                    $"{resolved.VersionLabel} — reload skipped, writing marker only");
                return (FamilyDocumentUpdateKind.PreVerified, name);
            }
            if (canCompare)
            {
                var a = embeddedHash!.Length > 8 ? embeddedHash[..8] : embeddedHash;
                var e = fileHash!.Length > 8 ? fileHash[..8] : fileHash;
                SmartConLogger.Info(
                    $"UpdateFamily[{catalogItemId}]: pre-verify — embedded '{name}' differs from the resolved file " +
                    $"{resolved.VersionLabel} (embedded {a}… ≠ file {e}…) — reload will run");
            }

            var capturedSource = fileDoc;
            var result = sourceAware.ReloadNestedInFamilyDocument(
                resolvedFullPath,
                name,
                overwriteParameterValues,
                preOpenedSourceDocProvider: () => capturedSource);

            // The source document is no longer needed — arbitration and
            // post-verify compare against the CACHED file hash. Retry the
            // file-hash extraction once when it failed earlier (transient),
            // then close the document BEFORE any further EditFamily: the M2
            // guard in ComputeEmbeddedVerificationHashOnRevitThread would
            // otherwise trip on our own background-open document (its Title
            // equals the family name) and every post-verify/arbitration would
            // return "not applicable" (validator finding, gate 2026-08-11).
            if (fileHash is null && fileDoc is not null)
            {
                try
                {
                    fileHash = ComputeFullFileHash(fileDoc);
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn(
                        $"UpdateFamily[{catalogItemId}]: file-hash retry after reload failed: " +
                        $"{ex.GetType().Name}: {ex.Message} " +
                        "[Action: верификация пропущена — повторите «Проверить»]");
                }
            }
            if (fileDoc is not null)
            {
                try { fileDoc.Close(false); } catch { }
                fileDoc = null;
            }

            if (!result.Success)
            {
                // Failed-reload arbitration: content may already match (the
                // "unchanged" false-failure) — treat as success.
                var (postArb, _) = ComputeEmbeddedVerificationHashOnRevitThread(doc, name, catalogItemId);
                if (postArb is not null && fileHash is not null
                    && string.Equals(postArb, fileHash, StringComparison.OrdinalIgnoreCase))
                {
                    SmartConLogger.Info(
                        $"UpdateFamily[{catalogItemId}]: reload reported failure but embedded content " +
                        $"matches catalog {resolved.VersionLabel} — treating as already up-to-date");
                    return (FamilyDocumentUpdateKind.Arbitrated, result.FamilyName ?? name);
                }
                return (FamilyDocumentUpdateKind.Failed, result.FamilyName ?? name);
            }

            if (fileHash is not null)
            {
                var (post, _) = ComputeEmbeddedVerificationHashOnRevitThread(doc, name, catalogItemId);
                if (post is null)
                {
                    // Same tri-state rule as the legacy policy: null is
                    // indeterminate (transient EditFamily/extract failure
                    // right after a successful merge) — trust the reload;
                    // the next Check/Update re-verifies.
                    SmartConLogger.Info(
                        $"UpdateFamily[{catalogItemId}]: post-verify indeterminate for '{name}' — trusting the successful reload");
                }
                else if (!string.Equals(post, fileHash, StringComparison.OrdinalIgnoreCase))
                {
                    var a = post.Length > 8 ? post[..8] : post;
                    var e = fileHash.Length > 8 ? fileHash[..8] : fileHash;
                    SmartConLogger.Warn(
                        $"UpdateFamily[{catalogItemId}]: POST-RELOAD VERIFICATION FAILED for '{name}' — embedded content " +
                        $"does not match the resolved file {resolved.VersionLabel} (embedded hash {a}… ≠ file {e}…). " +
                        "No marker is written; the family stays stale. " +
                        "[Action: обновление не заменило дефиницию — откройте родительское " +
                        "семейство в редакторе, удалите проблемное вложенное и загрузите его заново из каталога (привязки " +
                        "придётся восстановить), либо пересоберите родителя; затем повторите «Проверить»]");
                    return (FamilyDocumentUpdateKind.Failed, result.FamilyName ?? name);
                }
                else
                {
                    SmartConLogger.Info(
                        $"UpdateFamily[{catalogItemId}]: verified — embedded '{name}' matches the resolved file ({resolved.VersionLabel})");
                }
            }

            return (FamilyDocumentUpdateKind.Reloaded, result.FamilyName ?? name);
        }
        finally
        {
            try { fileDoc?.Close(false); } catch { }
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
