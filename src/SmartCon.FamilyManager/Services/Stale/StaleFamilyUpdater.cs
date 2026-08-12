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
        IFamilyContentHasher? contentHasher = null)
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
                return (false, null);
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
                        return (false, orchestrated.FamilyName);
                    }
                    await WriteMarkerBestEffortAsync(catalogItemId, resolved, orchestrated.FamilyName, targetRevit, ct)
                        .ConfigureAwait(true);
                    return (true, orchestrated.FamilyName);
                }
            }

            // #209 round-3: pre-verify BEFORE any reload — FAMILY-DOCUMENT
            // context ONLY (the verify returns "not applicable" in a
            // project, where the preserve-types reload is the proven path
            // and must always run). Case covered: the previous batch
            // already reloaded the nested family (a retry's LoadFamily
            // then returns false for "unchanged" and used to be reported
            // as a hard failure). If the embedded content already equals
            // the catalog target, the update is a no-op success — write
            // the marker without touching Revit.
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
                return (true, embeddedName);
            }

            // Issue #101: Stale Update must reload the family while preserving
            // the set of types currently loaded in the project. A plain
            // LoadFamily pulls in EVERY type defined in the .rfa (could be 50),
            // even if the user originally loaded only one via LoadFamilySymbol.
            // ReloadFamilyPreservingLoadedTypesAsync snapshots the loaded
            // symbols first and calls LoadFamilySymbol per type. When the
            // family is not loaded yet (no symbols to preserve) it falls back
            // to a full LoadFamilyAsync internally.
            var result = await _awaitable.RaiseAsync(
                _ => _loadService.ReloadFamilyPreservingLoadedTypesAsync(
                    resolved,
                    overwriteParameterValues,
                    onStatusMessage: null,
                    onSharedDecision: req => _dialogService.ShowSharedFamiliesLoadModeDialog(req),
                    nestedSharedNames: nestedNames,
                    ct: ct).GetAwaiter().GetResult(),
                ct).ConfigureAwait(true);

            if (!result.Success)
            {
                // In a FAMILY document LoadFamily also reports failure when
                // the file content is UNCHANGED versus what is loaded
                // (callbacks never fire) — indistinguishable from a real
                // rejection at this point, so let the content verification
                // arbitrate. In a PROJECT the verify is not applicable and
                // the failure stands as-is (a rejected reload must never be
                // masked as success).
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
                    return (true, result.FamilyName);
                }
                return (false, result.FamilyName);
            }

            // #209 (manual-test bug): in a FAMILY document the reload used
            // to be a silent no-op while markers claimed the new version.
            // The load service now reloads the nested definition via the
            // poke + doc-to-doc path (path-load fallback) — verify the
            // embedded content hash equals the catalog target version
            // BEFORE writing any marker.
            // A failed verification = failed update (no marker, the family
            // stays stale and the next Check offers it again).
            if (canVerify)
            {
                var verified = await VerifyEmbeddedMatchesResolvedFileAsync(
                        catalogItemId, resolved, result.FamilyName, ct)
                    .ConfigureAwait(true);
                if (StaleUpdateVerificationPolicy.ShouldFailSuccessfulReload(verified))
                {
                    return (false, result.FamilyName);
                }
            }

            await WriteMarkerBestEffortAsync(catalogItemId, resolved, result.FamilyName, targetRevit, ct)
                .ConfigureAwait(true);

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

    private async Task<(bool success, string? familyName)> UpdateSystemFamilyCoreAsync(
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
            return (false, item.Name);
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
            return (false, item.Name);
        }

        return (true, item.Name);
    }

    /// <summary>
    /// #209 round-4: content verification for the FAMILY-document context.
    /// Compares the VERIFICATION-GRADE hash
    /// (<see cref="IFamilyContentHasher.ComputeForEmbeddedVerification"/>)
    /// of the nested family embedded in the active family document against
    /// the same-grade hash of the resolved source .rfa — both extracted in
    /// the same live session, so the comparison is context-consistent, and
    /// the verification grade ignores regen-driven geometry metrics
    /// (volumes/bounds/areas/curve lengths) that legitimately differ when
    /// the host drives the nested family's instance parameters. The full
    /// identity FHV9 stored in the catalog is NOT used here: it is
    /// computed from the raw file, and a host-driven embedded definition
    /// can never equal it (the round-3 bug — false failures on families
    /// whose reload actually landed).
    /// Tri-state: <c>true</c> — embedded matches the file; <c>false</c> —
    /// differs (the reload did not land and the update MUST fail before
    /// any marker is written); <c>null</c> — NOT APPLICABLE / indeterminate
    /// (active document is a PROJECT — the preserve-types reload there is
    /// the long-proven path; file unreadable; family not nested in the
    /// document; open in the editor). Callers must distinguish via
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
            var doc = _revitContext.GetDocument();
            if (doc is null || !doc.IsFamilyDocument)
            {
                return (null, null); // project context — the proven path, no verify
            }

            var embedded = ComputeEmbeddedVerificationHashOnRevitThread(doc, name, catalogItemId);
            if (embedded is null)
            {
                return (null, null);
            }

            // C1 (validator): OpenDocumentFile of an ALREADY-OPEN file
            // returns the user's live document — hashing it would read
            // unsaved edits, and the finally-block Close(false) would
            // destroy them. Detect by PathName up front and bail out.
            var resolvedFullPath = System.IO.Path.GetFullPath(resolved.AbsolutePath);
            var alreadyOpen = doc.Application.Documents
                .Cast<Document>()
                .Any(d => !string.IsNullOrEmpty(d.PathName)
                    && string.Equals(
                        System.IO.Path.GetFullPath(d.PathName), resolvedFullPath, StringComparison.OrdinalIgnoreCase));
            if (alreadyOpen)
            {
                SmartConLogger.Warn(
                    $"UpdateFamily[{catalogItemId}]: the resolved file is open in the editor — " +
                    "its on-disk content cannot be trusted for verification " +
                    "[Action: закройте файл версии в редакторе (сохранив или отменив правки) и повторите «Обновить»]");
                return (null, null);
            }

            string? file = null;
            Document? fileDoc = null;
            try
            {
                fileDoc = doc.Application.OpenDocumentFile(resolved.AbsolutePath);
                var snap = _snapshotExtractor!.ExtractFromFamilyDocument(fileDoc);
                file = _contentHasher!.ComputeForEmbeddedVerification(snap)?.HexString;
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"UpdateFamily[{catalogItemId}]: could not extract the resolved file for verification: " +
                    $"{ex.GetType().Name}: {ex.Message} " +
                    "[Action: верификация пропущена — проверьте, что файл версии доступен на диске]");
                return (null, null);
            }
            finally
            {
                try { fileDoc?.Close(false); } catch { }
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
    /// Verification-grade hash of a family nested inside an open family
    /// document: EditFamily → snapshot →
    /// <see cref="IFamilyContentHasher.ComputeForEmbeddedVerification"/>.
    /// Returns <c>null</c> (verification not applicable) when the family
    /// is not found or is open as a top-level document (EditFamily would
    /// return the user's live document with unsaved edits, and closing it
    /// would destroy them — skip instead; each case is logged with an
    /// action).
    /// </summary>
    private string? ComputeEmbeddedVerificationHashOnRevitThread(
        Document doc, string familyName, string catalogItemId)
    {
        var nested = new FilteredElementCollector(doc)
            .OfClass(typeof(Autodesk.Revit.DB.Family))
            .Cast<Autodesk.Revit.DB.Family>()
            .FirstOrDefault(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase));
        if (nested is null)
        {
            SmartConLogger.Warn(
                $"UpdateFamily[{catalogItemId}]: family '{familyName}' not found in the family document " +
                "[Action: верификация пропущена — семейство не вложено в активный документ]");
            return null;
        }

        // Guard: the family is open as a top-level document — EditFamily
        // would return the user's live document (unsaved edits), and the
        // finally-block Close(false) would destroy them. Match by Title
        // (file name) AND by OwnerFamily name (renamed files), M2.
        var isOpenTopLevel = doc.Application.Documents
            .Cast<Document>()
            .Any(d => d.IsFamilyDocument
                && (string.Equals(d.Title, familyName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(d.OwnerFamily?.Name, familyName, StringComparison.OrdinalIgnoreCase)));
        if (isOpenTopLevel)
        {
            SmartConLogger.Warn(
                $"UpdateFamily[{catalogItemId}]: '{familyName}' is open in the Family Editor — embedded content cannot be trusted " +
                "[Action: закройте семейство в редакторе (сохранив или отменив правки) и повторите «Обновить»]");
            return null;
        }

        Document? copy = null;
        try
        {
            copy = doc.EditFamily(nested);
            var snapshot = _snapshotExtractor!.ExtractFromFamilyDocument(copy);
            return _contentHasher!.ComputeForEmbeddedVerification(snapshot)?.HexString;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"UpdateFamily[{catalogItemId}]: embedded hash computation failed for '{familyName}': {ex.GetType().Name}: {ex.Message} " +
                "[Action: верификация пропущена — семейство останется stale, повторите «Обновить»]");
            return null;
        }
        finally
        {
            try { copy?.Close(false); } catch { }
        }
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

        var embeddedHash = ComputeEmbeddedVerificationHashOnRevitThread(doc, name, catalogItemId);

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
        try
        {
            fileDoc = doc.Application.OpenDocumentFile(resolved.AbsolutePath);
            var snap = _snapshotExtractor!.ExtractFromFamilyDocument(fileDoc);
            fileHash = _contentHasher!.ComputeForEmbeddedVerification(snap)?.HexString;
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
                    var snap = _snapshotExtractor!.ExtractFromFamilyDocument(fileDoc);
                    fileHash = _contentHasher!.ComputeForEmbeddedVerification(snap)?.HexString;
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
                var postArb = ComputeEmbeddedVerificationHashOnRevitThread(doc, name, catalogItemId);
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
                var post = ComputeEmbeddedVerificationHashOnRevitThread(doc, name, catalogItemId);
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
