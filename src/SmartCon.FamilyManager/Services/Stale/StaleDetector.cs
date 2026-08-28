using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.Stale;

/// <summary>
/// On-demand stale detector (ADR-030, Issue #69). Reads <see cref="FamilyVersion"/>
/// markers from the active project via <see cref="IFamilyVersionStore"/> and compares
/// them with the catalog. Maintains a session-scoped cache that is **merged** on each
/// <c>Check</c> (other categories are preserved) and **pruned** on successful Update
/// (only the updated families are dropped). Edit / DB-switch invalidate the whole cache
/// (D-10).
/// </summary>
internal sealed class StaleDetector : IStaleDetector
{
    private readonly IFamilyVersionStore _store;
    private readonly IFamilyCatalogProvider _catalog;
    private readonly IFamilyManagerAwaitableEvent _awaitable;
    private readonly IRevitContext _revitContext;
    private readonly IClock _clock;
    private readonly ISystemTypeFinder _systemTypeFinder;
    private readonly ISystemTypeVersionStore _systemTypeStore;
    private readonly IFamilyTypeRepository _typeRepository;
    private readonly IFamilyFileResolver? _fileResolver;
    private readonly IFamilySnapshotExtractor? _snapshotExtractor;
    private readonly IFamilyContentHasher? _contentHasher;
    private readonly IFamilyVersionWriter? _versionWriter;
    private readonly IContentHashAnalyticsRepository? _contentHashAnalytics;
    private FamilyStaleSnapshot? _cachedSnapshot;
    private readonly object _cacheLock = new();
    /// <summary>#187: per-type stale verdicts for system items —
    /// catalogItemId → (typeKey "FAMILY|NAME" upper → isStale). Feeds the
    /// orange presence dot on the exact outdated type node.</summary>
    private readonly Dictionary<string, Dictionary<string, bool>> _systemTypeStaleByType = new(StringComparer.Ordinal);
    /// <summary>#249 (Phase 2): per-type stale verdicts for LOADABLE items —
    /// catalogItemId → (original type name, OrdinalIgnoreCase → isStale).
    /// Filled only when the content verification produced a per-type proof;
    /// an absent entry means "no per-type data" and the tree falls back to
    /// the family-level (leaf-scoped) dot — the pre-#249 behaviour.</summary>
    private readonly Dictionary<string, Dictionary<string, bool>> _loadableTypeStaleByType = new(StringComparer.Ordinal);

    public StaleDetector(
        IFamilyVersionStore store,
        IFamilyCatalogProvider catalog,
        IFamilyManagerAwaitableEvent awaitable,
        IRevitContext revitContext,
        IClock clock,
        ISystemTypeFinder systemTypeFinder,
        ISystemTypeVersionStore systemTypeStore,
        IFamilyTypeRepository typeRepository,
        IFamilyFileResolver? fileResolver = null,
        IFamilySnapshotExtractor? snapshotExtractor = null,
        IFamilyContentHasher? contentHasher = null,
        IFamilyVersionWriter? versionWriter = null,
        IContentHashAnalyticsRepository? contentHashAnalytics = null)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(awaitable);
        ArgumentNullException.ThrowIfNull(revitContext);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(systemTypeFinder);
        ArgumentNullException.ThrowIfNull(systemTypeStore);
        ArgumentNullException.ThrowIfNull(typeRepository);
#else
        if (store is null) throw new ArgumentNullException(nameof(store));
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));
        if (awaitable is null) throw new ArgumentNullException(nameof(awaitable));
        if (revitContext is null) throw new ArgumentNullException(nameof(revitContext));
        if (clock is null) throw new ArgumentNullException(nameof(clock));
        if (systemTypeFinder is null) throw new ArgumentNullException(nameof(systemTypeFinder));
        if (systemTypeStore is null) throw new ArgumentNullException(nameof(systemTypeStore));
        if (typeRepository is null) throw new ArgumentNullException(nameof(typeRepository));
#endif
        _store = store;
        _catalog = catalog;
        _awaitable = awaitable;
        _revitContext = revitContext;
        _clock = clock;
        _systemTypeFinder = systemTypeFinder;
        _systemTypeStore = systemTypeStore;
        _typeRepository = typeRepository;
        _fileResolver = fileResolver;
        _snapshotExtractor = snapshotExtractor;
        _contentHasher = contentHasher;
        _versionWriter = versionWriter;
        _contentHashAnalytics = contentHashAnalytics;
    }

    public async Task<StaleCheckResult> CheckFamilyAsync(
        string catalogItemId,
        string familyName,
        Document doc,
        ElementId familyId,
        CancellationToken ct)
    {
        using var _scope = SmartConLogger.BeginScope(
            "StaleDetection",
            ("Method", nameof(CheckFamilyAsync)),
            ("CatalogItemId", catalogItemId));

#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(familyId);
#else
        if (doc is null) throw new ArgumentNullException(nameof(doc));
        if (familyId is null) throw new ArgumentNullException(nameof(familyId));
#endif

        var catalogItem = await _catalog.GetItemAsync(catalogItemId, ct).ConfigureAwait(false);
        StaleCheckResult result;
        if (catalogItem is null)
        {
            result = new StaleCheckResult(catalogItemId, familyName, null, null, true, StaleReason.NotInCatalog);
        }
        else
        {
            // ES read must run on Revit main thread (I-01). Use RaiseAsync to marshal.
            var loaded = await _awaitable.RaiseAsync(
                _ => _store.ReadFromLoadedFamily(doc, familyId),
                ct).ConfigureAwait(true);

            var targetRevit = ResolveTargetRevit();
            var reason = loaded is null
                ? StaleReason.NoEntityStorage
                : SystemTypeStaleLogic.ComputeReason(
                    loaded, catalogItem.Id, catalogItem.CurrentVersionLabel, targetRevit);

            // The same content-verification rule as the category check
            // (#180 + #218): a marker that cannot speak (missing / matches
            // current / orphaned id) → prove content, heal on match.
            reason = await RefineReasonByContentAsync(
                catalogItem, familyName, loaded, reason, doc, targetRevit,
                new Dictionary<string, EmbeddedContentVerifier.FileProof>(StringComparer.OrdinalIgnoreCase), ct)
                .ConfigureAwait(true);

            result = new StaleCheckResult(
                catalogItemId, familyName,
                catalogItem.CurrentVersionLabel, loaded?.VersionLabel,
                IsStale: reason != StaleReason.None, Reason: reason);
        }

        // Single-family check updates the snapshot entry for this family only,
        // leaving all other entries intact.
        MergeSingleResult(result);
        return result;
    }

    public async Task<IReadOnlyList<StaleCheckResult>> CheckCategoryAsync(
        IReadOnlyList<string>? categoryIds,
        Document doc,
        CancellationToken ct)
    {
        var scopeCategoryIds = categoryIds is null
            ? "<all>"
            : string.Join(",", categoryIds);
        using var _scope = SmartConLogger.BeginScope(
            "StaleDetection",
            ("Method", nameof(CheckCategoryAsync)),
            ("CategoryIds", scopeCategoryIds));

#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(doc);
#else
        if (doc is null) throw new ArgumentNullException(nameof(doc));
#endif

        // 1) Read catalog items for the given categories (SQLite, async, no Revit API).
        // "__no_category__" is the synthetic ID for the uncategorized node;
        // the actual SQL filter must match NULL/empty category_id. If the list
        // contains BOTH "__no_category__" and real categories, IncludeUncategorized
        // is true AND CategoryIdsFilter carries the real IDs — the SQL builder
        // (LocalCatalogQueryBuilder) now composes them with OR, not else-if.
        // For "Check all" (categoryIds == null), IncludeUncategorized stays
        // false so every catalog row is matched.
        var hasUncategorized = categoryIds is { Count: > 0 } &&
            categoryIds.Any(id => id == "__no_category__");
        var hasFilter = categoryIds is { Count: > 0 };
        var singleCategoryId = hasFilter && categoryIds!.Count == 1 && !hasUncategorized
            ? categoryIds[0]
            : null;
        var realCategoryIds = hasFilter
            ? categoryIds!.Where(id => id != "__no_category__").ToList()
            : null;
        var hasRealFilter = realCategoryIds is { Count: > 0 };

        var query = new FamilyCatalogQuery(
            SearchText: null,
            CategoryFilter: singleCategoryId,
            StatusFilter: null,
            Tags: null,
            Sort: FamilyCatalogSort.NameAsc,
            Offset: 0,
            Limit: int.MaxValue,
            IncludeUncategorized: hasUncategorized,
            CategoryIdsFilter: hasRealFilter ? realCategoryIds : null);
        var catalogItems = await _catalog.SearchAsync(query, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (catalogItems.Count == 0) return Array.Empty<StaleCheckResult>();

        // Issue #104: system catalog items are matched by (type name,
        // category) against ElementType markers — a completely different
        // mechanism from loadable families (Family element by name).
        var loadableItems = catalogItems
            .Where(i => i.FamilySource != "system")
            .ToList();
        var systemItems = catalogItems
            .Where(i => i.FamilySource == "system")
            .ToList();

        var results = new List<StaleCheckResult>();
        var targetRevit = ResolveTargetRevit();

        if (loadableItems.Count > 0)
        {
            var loadableResults = await CheckLoadableItemsAsync(loadableItems, doc, targetRevit, ct)
                .ConfigureAwait(true);
            results.AddRange(loadableResults);
        }

        if (systemItems.Count > 0)
        {
            var systemResults = await CheckSystemItemsAsync(systemItems, doc, targetRevit, ct)
                .ConfigureAwait(true);
            results.AddRange(systemResults);
        }

        if (results.Count == 0) return Array.Empty<StaleCheckResult>();

        // 6) Merge into session cache: existing entries for OTHER families are preserved.
        lock (_cacheLock)
        {
            var before = _cachedSnapshot?.Results.Count ?? 0;
            _cachedSnapshot = StaleSnapshotLogic.MergeInto(_cachedSnapshot, results, _clock.UtcNow);
            var staleNow = results.Count(r => r.IsStale);
            SmartConLogger.Info(
                $"CheckCategory: merged {results.Count} results ({staleNow} stale). " +
                $"Snapshot size: {before} -> {_cachedSnapshot.Results.Count}.");
        }

        return results;
    }

    private async Task<IReadOnlyList<StaleCheckResult>> CheckLoadableItemsAsync(
        IReadOnlyList<FamilyCatalogItem> loadableItems,
        Document doc,
        int targetRevit,
        CancellationToken ct)
    {
        // 2) Collect Family element ids from the active document on the Revit thread.
        // Two families with the same Name are rare but possible (e.g. two
        // loadable variants both loaded). We use a list, log a warning, and
        // return all candidates — the catalog item matches the FIRST one, but
        // the operator is told there is an ambiguity. Without this, multiple
        // matches silently overwrite each other.
        var familyIds = await _awaitable.RaiseAsync(
            _ =>
            {
                var map = new Dictionary<string, List<(string FamilyName, ElementId Id)>>(StringComparer.Ordinal);
                using var collector = new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Family));
                foreach (Autodesk.Revit.DB.Family f in collector)
                {
                    if (f is null || f.Name is null) continue;
                    if (!map.TryGetValue(f.Name, out var list))
                    {
                        list = new List<(string, ElementId)>();
                        map[f.Name] = list;
                    }
                    list.Add((f.Name, f.Id));
                }
                foreach (var kvp in map)
                {
                    if (kvp.Value.Count > 1)
                    {
                        var firstId = kvp.Value[0].Id;
#if NET8_0_OR_GREATER
                        var firstIdValue = firstId.Value;
#else
#pragma warning disable CS0618 // IntegerValue is deprecated in Revit 2024; removed in 2025. Use Value when available.
                        var firstIdValue = firstId.IntegerValue;
#pragma warning restore CS0618
#endif
                        SmartConLogger.Warn(
                            $"CheckCategory: family name '{kvp.Value[0].FamilyName}' matches {kvp.Value.Count} " +
                            $"Family elements in the project; the first match (ElementId=" +
                            $"{firstIdValue}) will be used. " +
                            "[Action: rename one of the families to remove the ambiguity]");
                    }
                }
                return map;
            }, ct).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();

        // 3) Match catalog items to Revit Family elements by name (left join).
        var matched = new List<(FamilyCatalogItem Item, string FamilyName, ElementId Id)>();
        var matchCounter = new HotLoopCounter(sampleEvery: 32);
        foreach (var item in loadableItems)
        {
            if (matchCounter.ShouldLog())
            {
                SmartConLogger.Debug(
                    $"Matching {matchCounter.Count}/{loadableItems.Count}: '{item.Name}'.");
            }
            if (familyIds.TryGetValue(item.Name, out var hits) && hits.Count > 0)
            {
                matched.Add((item, hits[0].FamilyName, hits[0].Id));
            }
        }

        if (matched.Count == 0) return Array.Empty<StaleCheckResult>();

        // 4) Batch ES read on the Revit thread.
        var ids = matched.Select(m => m.Id).ToList();
        var versions = await _awaitable.RaiseAsync(
            _ => _store.ReadManyFromDocument(doc, ids),
            ct).ConfigureAwait(true);

        // 5) Compute StaleReason for each.
        var results = new List<StaleCheckResult>(matched.Count);
        var reasonCounter = new HotLoopCounter(sampleEvery: 32);
        // Content verification (#180, owner decision 2026-08-12; #218 orphan
        // markers): the check proves CONTENT whenever the marker alone cannot
        // speak — missing entirely, matching the current version (local edits
        // become ContentDrift), or pointing at an orphaned catalog id (the
        // item was re-imported under a new id; heal re-resolves it). The
        // unified FHV10 hash is a fair comparison for an embedded / loaded
        // copy: the one field a merge physically cannot transfer (parameter
        // groups) is not hashed at all, and everything else in the embedded
        // EditFamily document is byte-identical to the source file (probes
        // 2026-08-12). A label drift without id mismatch is stale WITHOUT
        // opening any document — «Обновить» reconciles it.
        // One OpenDocumentFile per version FILE per check run — duplicate
        // catalog items resolving to the same file share the cached proof
        // (family hash + per-type hashes; a cached null-field entry is a
        // cached "indeterminate").
        var fileProofCache = new Dictionary<string, EmbeddedContentVerifier.FileProof>(StringComparer.OrdinalIgnoreCase);
        foreach (var (item, familyName, id) in matched)
        {
            versions.TryGetValue(id, out var loaded);
            var reason = loaded is null
                ? StaleReason.NoEntityStorage
                : SystemTypeStaleLogic.ComputeReason(
                    loaded, item.Id, item.CurrentVersionLabel, targetRevit);

            reason = await RefineReasonByContentAsync(
                item, familyName, loaded, reason, doc, targetRevit, fileProofCache, ct)
                .ConfigureAwait(true);

            results.Add(new StaleCheckResult(
                item.Id, familyName,
                item.CurrentVersionLabel,
                loaded?.VersionLabel,
                reason != StaleReason.None,
                reason));

            if (reasonCounter.ShouldLog())
            {
                SmartConLogger.Debug(
                    $"Computed reasons for {reasonCounter.Count}/{matched.Count} families.");
            }
        }

        return results;
    }

    /// <summary>
    /// FHV10 content proof for an embedded nested family in a family
    /// document or project: <see cref="LoadableVerificationResult.Verdict"/>
    /// — <c>true</c> embedded content matches the current catalog version
    /// file, <c>false</c> differs, <c>null</c> indeterminate (file
    /// unresolvable, guards tripped) and the caller keeps the marker-based
    /// verdict. <see cref="LoadableVerificationResult.PerTypeStale"/> (#249,
    /// Phase 2) resolves WHICH loaded types drifted — it feeds the
    /// per-type orange dot in the tree.
    /// </summary>
    private async Task<LoadableVerificationResult> ContentVerifyEmbeddedAsync(
        FamilyCatalogItem item,
        string familyName,
        Document doc,
        int targetRevit,
        IDictionary<string, EmbeddedContentVerifier.FileProof> fileProofCache,
        CancellationToken ct)
    {
        FamilyResolvedFile resolved;
        try
        {
            resolved = await _fileResolver!.ResolveForLoadAsync(item.Id, targetRevit, ct)
                .ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SmartConLogger.Warn(
                $"CheckEmbedded[{item.Id}]: failed to resolve the current version file: {ex.GetType().Name}: {ex.Message} " +
                "[Action: контентная верификация пропущена — проверьте, что файл версии доступен на диске]");
            return new LoadableVerificationResult(null, null);
        }
        if (string.IsNullOrEmpty(resolved.AbsolutePath))
        {
            return new LoadableVerificationResult(null, null);
        }

        return await _awaitable.RaiseAsync(
            _ => EmbeddedContentVerifier.VerifyEmbeddedAgainstFileDetailed(
                doc, familyName, resolved.AbsolutePath,
                _snapshotExtractor!, _contentHasher!, $"CheckEmbedded[{item.Id}]", fileProofCache),
            ct).ConfigureAwait(true);
    }

    /// <summary>
    /// Writes the current-version marker onto an embedded family whose
    /// content was just proven current — the next check takes the fast
    /// marker path. Best-effort: the check verdict is already correct even
    /// when the write fails.
    /// </summary>
    private async Task HealMarkerBestEffortAsync(
        FamilyCatalogItem item,
        string familyName,
        int targetRevit,
        CancellationToken ct)
    {
        if (_versionWriter is null)
        {
            return;
        }
        try
        {
            await _versionWriter.WriteVersionMarkerAsync(
                item.Id, familyName, item.CurrentVersionLabel, targetRevit, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"CheckEmbedded[{item.Id}]: content verified but marker heal failed: {ex.GetType().Name}: {ex.Message} " +
                "[Action: вердикт корректен, но следующая «Проверить» снова выполнит контентную верификацию — проверьте ES-схему]");
        }
    }

    /// <summary>
    /// Refines the marker-based verdict whenever the marker alone cannot
    /// speak (#180 + #218): the marker is missing entirely, matches the
    /// current version (a local edit would stay invisible to marker-only
    /// checks), or points at a DIFFERENT catalog id. The id-mismatch case is
    /// split by a catalog lookup:
    /// <list type="bullet">
    /// <item><b>Orphaned</b> (#218): the referenced id no longer exists in
    /// the catalog (the item was deleted and re-imported under a new id).
    /// The marker cannot testify about the version — the embedded content is
    /// proven against the current version file instead, and on a match the
    /// marker is HEALED with the re-resolved id (Info level, no «corrupted»
    /// scare).</item>
    /// <item><b>Foreign</b>: the id belongs to another live catalog item —
    /// genuinely corrupted ES data. Warn + the stale verdict stands.</item>
    /// </list>
    /// Content-proof outcomes: <c>true</c> → not stale (missing/orphaned
    /// marker healed, best-effort); <c>false</c> → <see cref="StaleReason.ContentDrift"/>
    /// for a loaded family («Обновить» restores the catalog content), the
    /// no-marker verdict stands otherwise; <c>null</c> (indeterminate) → the
    /// marker-based verdict stands.
    /// </summary>
    private async Task<StaleReason> RefineReasonByContentAsync(
        FamilyCatalogItem item,
        string familyName,
        FamilyVersion? loaded,
        StaleReason reason,
        Document doc,
        int targetRevit,
        IDictionary<string, EmbeddedContentVerifier.FileProof> fileProofCache,
        CancellationToken ct)
    {
        // #218: orphan-vs-foreign classification of an id mismatch. The
        // lookup is cheap (one indexed SQLite read) and runs only for the
        // already-rare mismatch — never on the happy path.
        var markerOrphaned = false;
        if (loaded is not null
            && !string.IsNullOrEmpty(loaded.CatalogItemId)
            && !string.Equals(loaded.CatalogItemId, item.Id, StringComparison.Ordinal))
        {
            bool? referencedExists;
            try
            {
                referencedExists = await _catalog.GetItemAsync(loaded.CatalogItemId!, ct)
                    .ConfigureAwait(false) is not null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                referencedExists = null;
                SmartConLogger.Warn(
                    $"CheckEmbedded[{item.Id}]: failed to look up the marker's CatalogItemId in the catalog: " +
                    $"{ex.GetType().Name}: {ex.Message} " +
                    "[Action: orphan-резолв пропущен, маркер считается чужим — проверьте доступность БД каталога]");
            }

            if (referencedExists == false)
            {
                markerOrphaned = true;
                SmartConLogger.Info(
                    $"CheckEmbedded[{item.Id}]: '{familyName}' marker CatalogItemId='{loaded.CatalogItemId}' " +
                    "no longer exists in the catalog (the item was re-imported under a new id) — " +
                    "re-resolving by content");
            }
            else
            {
                SmartConLogger.Warn(
                    $"CheckEmbedded[{item.Id}]: '{familyName}' ES marker CatalogItemId='{loaded.CatalogItemId}' " +
                    $"does not match catalog id '{item.Id}'. " +
                    "[Action: ES data is corrupted for this family; treat as stale]");
            }
        }

        var canContentVerify = _fileResolver is not null
            && _snapshotExtractor is not null
            && _contentHasher is not null;
        var markerCannotSpeak = reason == StaleReason.NoEntityStorage
            || reason == StaleReason.None
            || markerOrphaned;
        if (!markerCannotSpeak || !canContentVerify)
        {
            // #249 (follow-up, manual test): a VersionMismatch marker is a
            // valid FAMILY-level verdict ("an older version is embedded"),
            // but it must NOT paint every type stale — the per-type answer
            // is computable from the catalog alone: per-type hashes of the
            // marker's version vs the current one. No document opens, no
            // EditFamily — immune to the open-editor guard that made the
            // embedded proof (and with it the per-type map) unavailable
            // right after "Import Active File".
            if (reason == StaleReason.VersionMismatch
                && loaded?.VersionLabel is not null
                && item.CurrentVersionLabel is not null
                && _contentHashAnalytics is not null)
            {
                StoreLoadableTypeStaleMap(item.Id,
                    await ComputeDbPerTypeStaleAsync(
                        item.Id, loaded.VersionLabel!, item.CurrentVersionLabel, ct)
                        .ConfigureAwait(false));
            }
            else
            {
                // No per-type proof without a content verification — clear any
                // stale map from a previous check so the tree falls back to the
                // family-level (leaf-scoped) dot (#249, Phase 2).
                ClearLoadableTypeStaleMap(item.Id);
            }
            return reason;
        }

        var verification = await ContentVerifyEmbeddedAsync(
            item, familyName, doc, targetRevit, fileProofCache, ct).ConfigureAwait(true);
        var verdict = verification.Verdict;

        if (verdict == true)
        {
            // A content match clears the per-type drift too — the tree
            // falls back to the (now non-stale) leaf verdict.
            ClearLoadableTypeStaleMap(item.Id);
            if (loaded is null || markerOrphaned)
            {
                SmartConLogger.Debug(
                    $"CheckEmbedded: '{familyName}' {(loaded is null ? "has no version marker" : "has an orphaned marker")} " +
                    $"but content matches {item.CurrentVersionLabel} — not stale (marker healed)");
                await HealMarkerBestEffortAsync(item, familyName, targetRevit, ct).ConfigureAwait(true);
            }
            else
            {
                SmartConLogger.Debug(
                    $"CheckEmbedded: '{familyName}' marker matches {item.CurrentVersionLabel}, " +
                    "content re-verify=matches — not stale");
            }
            return StaleReason.None;
        }

        if (verdict == false)
        {
            // #249 (Phase 2): keep the per-type drift map for the tree;
            // without a proof the entry is cleared (leaf-scoped fallback).
            StoreLoadableTypeStaleMap(item.Id, verification.PerTypeStale);
            if (loaded is null)
            {
                SmartConLogger.Debug(
                    $"CheckEmbedded: '{familyName}' has no version marker, " +
                    $"current={item.CurrentVersionLabel}, contentVerify=differs — verdict stays stale");
                return reason;
            }

            // Marker == current, but the content was edited locally (#180);
            // or an orphaned marker whose content is genuinely older than
            // the current version (#218). «Обновить» restores the catalog
            // content in both cases.
            var changedTypes = verification.PerTypeStale?.Count(kv => kv.Value) ?? 0;
            SmartConLogger.Debug(
                $"CheckEmbedded: '{familyName}' {(markerOrphaned ? "orphaned marker" : $"marker matches {item.CurrentVersionLabel}")} " +
                $"but the content DIFFERS from the current catalog version — stale (ContentDrift, {changedTypes} changed type(s))");
            return StaleReason.ContentDrift;
        }

        ClearLoadableTypeStaleMap(item.Id);
        SmartConLogger.Debug(
            $"CheckEmbedded: '{familyName}' content verify indeterminate " +
            $"— marker-based verdict stands ({reason})");
        return reason;
    }

    /// <summary>
    /// #249 (Phase 2): stores the per-type drift map of a loadable item
    /// (a null proof clears the entry → the tree falls back to the
    /// family-level dot). Threading: same discipline as
    /// <see cref="_systemTypeStaleByType"/> — mutated under
    /// <see cref="_cacheLock"/>.
    /// </summary>
    private void StoreLoadableTypeStaleMap(string catalogItemId, IReadOnlyDictionary<string, bool>? perTypeStale)
    {
        lock (_cacheLock)
        {
            if (perTypeStale is null)
            {
                _loadableTypeStaleByType.Remove(catalogItemId);
            }
            else
            {
                // net48: Dictionary has no IReadOnlyDictionary ctor —
                // project via LINQ instead.
                _loadableTypeStaleByType[catalogItemId] = perTypeStale
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// #249 (follow-up, manual test): per-type drift map for a VersionMismatch
    /// verdict, computed PURELY from the catalog DB — per-type content hashes
    /// of the embedded (marker) version vs the current version. Types whose
    /// hashes match are NOT stale (their embedded content equals the current
    /// version's, the family-level verdict notwithstanding); types removed
    /// in the current version are stale; types added in the current version
    /// are not (the project cannot have them). <c>null</c> when either
    /// version's analytics are pending — the tree then keeps the pre-fix
    /// leaf-scoped fallback.
    /// <para>
    /// Round-5 fix: per-type hashes track per-type VALUES only — a change in
    /// a SHARED section (GEOM, DEF, CONN, …) affects EVERY type, and a
    /// values-only map showed "0 stale" while the family genuinely needed a
    /// reload (geometry dots vanished). The section hashes of the two
    /// versions are compared too: any changed section outside the per-type
    /// ones (<c>TYPES</c>/<c>VALUES</c>) marks every loaded type stale.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyDictionary<string, bool>?> ComputeDbPerTypeStaleAsync(
        string catalogItemId,
        string fromVersionLabel,
        string currentVersionLabel,
        CancellationToken ct)
    {
        try
        {
            var from = await _contentHashAnalytics!.GetTypeHashesAsync(catalogItemId, fromVersionLabel, ct)
                .ConfigureAwait(false);
            var to = await _contentHashAnalytics.GetTypeHashesAsync(catalogItemId, currentVersionLabel, ct)
                .ConfigureAwait(false);
            if (from is null || to is null)
            {
                SmartConLogger.Debug(
                    $"CheckEmbedded[{catalogItemId}]: per-type analytics pending for " +
                    $"{fromVersionLabel} or {currentVersionLabel} — leaf-scoped stale fallback");
                return null;
            }

            // Shared-section rule (round 5): a change outside the per-type
            // sections makes EVERY loaded type stale.
            var sharedChangedSections = await ComputeChangedSharedSectionsAsync(
                catalogItemId, fromVersionLabel, currentVersionLabel, ct).ConfigureAwait(false);

            var toByKey = new Dictionary<string, FamilyTypeHashEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in to)
            {
                toByKey[entry.TypeIdentityKey] = entry;
            }

            var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in from)
            {
                map[entry.TypeName] = sharedChangedSections.Count > 0
                    || !toByKey.TryGetValue(entry.TypeIdentityKey, out var other)
                    || !string.Equals(entry.HashHex, other.HashHex, StringComparison.OrdinalIgnoreCase);
            }
            foreach (var entry in to)
            {
                // New in the current version — the project cannot carry it.
                // (net48: Dictionary has no TryAdd.)
                if (!map.ContainsKey(entry.TypeName))
                {
                    map[entry.TypeName] = false;
                }
            }

            SmartConLogger.Debug(
                $"CheckEmbedded[{catalogItemId}]: DB per-type drift between {fromVersionLabel} and " +
                $"{currentVersionLabel}: {map.Count(kv => kv.Value)} stale of {map.Count} type(s)" +
                (sharedChangedSections.Count > 0
                    ? $" (shared sections changed: {string.Join(", ", sharedChangedSections)})"
                    : string.Empty));
            return map;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SmartConLogger.Warn(
                $"CheckEmbedded[{catalogItemId}]: DB per-type drift computation failed: {ex.Message} " +
                "[Action: per-type индикация отключена для этого семейства до следующей «Проверить»; повторите проверку]");
            return null;
        }
    }

    /// <summary>
    /// Section names whose hashes differ between two versions of the item,
    /// excluding the per-type sections (<c>TYPES</c>/<c>VALUES</c> — those
    /// are answered by the per-type hash comparison). Empty when the
    /// section analytics are pending for either version (the values-level
    /// answer then stands alone) or when nothing shared changed.
    /// </summary>
    private async Task<IReadOnlyList<string>> ComputeChangedSharedSectionsAsync(
        string catalogItemId,
        string fromVersionLabel,
        string currentVersionLabel,
        CancellationToken ct)
    {
        var fromSections = await _contentHashAnalytics!.GetSectionHashesAsync(catalogItemId, fromVersionLabel, ct)
            .ConfigureAwait(false);
        var toSections = await _contentHashAnalytics.GetSectionHashesAsync(catalogItemId, currentVersionLabel, ct)
            .ConfigureAwait(false);
        if (fromSections is null || toSections is null)
        {
            return Array.Empty<string>();
        }

        var changed = new List<string>();
        foreach (var key in fromSections.Keys.Concat(toSections.Keys).Distinct(StringComparer.Ordinal))
        {
            if (string.Equals(key, FamilyContentSectionNames.Types, StringComparison.Ordinal)
                || string.Equals(key, FamilyContentSectionNames.Values, StringComparison.Ordinal))
            {
                continue;
            }
            if (!fromSections.TryGetValue(key, out var a)
                || !toSections.TryGetValue(key, out var b)
                || !string.Equals(a, b, StringComparison.Ordinal))
            {
                changed.Add(key);
            }
        }
        return changed;
    }

    private void ClearLoadableTypeStaleMap(string catalogItemId)
    {
        lock (_cacheLock)
        {
            _loadableTypeStaleByType.Remove(catalogItemId);
        }
    }

    /// <summary>
    /// System-family branch of <see cref="CheckCategoryAsync"/> (Issue #104):
    /// matches each system catalog item's types (from <c>family_types</c>)
    /// against project <c>ElementType</c> elements by (type name, category
    /// ordinal) and aggregates one verdict per item.
    /// </summary>
    private async Task<IReadOnlyList<StaleCheckResult>> CheckSystemItemsAsync(
        IReadOnlyList<FamilyCatalogItem> systemItems,
        Document doc,
        int targetRevit,
        CancellationToken ct)
    {
        var typeNamesByItem = await _typeRepository
            .GetAllTypesBatchAsync(systemItems.Select(i => i.Id), ct)
            .ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var categoryOrdinals = systemItems
            .Select(i => i.RevitCategoryId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        if (categoryOrdinals.Count == 0) return Array.Empty<StaleCheckResult>();

        var projectTypes = await _awaitable.RaiseAsync(
            _ => _systemTypeFinder.CollectTypes(doc, categoryOrdinals),
            ct).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();

        var typesByCategoryAndName = new Dictionary<(int Category, string Name), List<(string? FamilyKey, string? Family, ElementId Id)>>();
        foreach (var location in projectTypes)
        {
            // Names are matched case-insensitively (same semantics as
            // ISystemTypeFinder.FindTypeByName) — the tuple key carries the
            // upper-invariant form so OrdinalIgnoreCase applies to lookups.
            // #183: one (category, name) key maps to a LIST of candidates —
            // "Стандарт" exists in both conduit families; the descriptor's
            // family disambiguates (legacy null family → first candidate,
            // the pre-#183 behaviour).
            // #190 (ADR-064): each candidate carries the locale-invariant
            // key AND the localized name — descriptors with a key match by
            // key, legacy descriptors fall back to the name.
            var key = (location.CategoryOrdinal, location.TypeName.ToUpperInvariant());
            if (!typesByCategoryAndName.TryGetValue(key, out var candidates))
            {
                candidates = new List<(string?, string?, ElementId)>();
                typesByCategoryAndName[key] = candidates;
            }
            candidates.Add((location.FamilyKey, location.FamilyName, location.TypeId));
        }

        var matched = new List<(FamilyCatalogItem Item, List<ElementId> TypeIds)>();
        // Audit (B1): items that previously matched but whose types vanished
        // from the project — their snapshot entries must be REMOVED, else
        // MergeInto keeps the old stale verdict (phantom badge until
        // InvalidateCache).
        var vanishedItemIds = new List<string>();
        // #187: per-descriptor matches kept for the per-type stale map
        // (orange presence dot) — the aggregate verdict alone loses which
        // concrete type is outdated.
        var matchedByDescriptor = new List<(FamilyCatalogItem Item, FamilyTypeDescriptor Descriptor, ElementId TypeId)>();
        foreach (var item in systemItems)
        {
            if (!item.RevitCategoryId.HasValue) continue;
            if (!typeNamesByItem.TryGetValue(item.Id, out var descriptors) || descriptors.Count == 0)
                continue;

            var ids = new List<ElementId>();
            foreach (var descriptor in descriptors)
            {
                if (!typesByCategoryAndName.TryGetValue(
                        (item.RevitCategoryId.Value, descriptor.Name.ToUpperInvariant()), out var candidates))
                {
                    continue;
                }

                if (descriptor.FamilyKey is not null || descriptor.FamilyName is not null)
                {
                    // Exact identity match — never read the marker of a
                    // foreign family's type. #190 (ADR-064): key first
                    // (locale-invariant), legacy descriptors (pre-V27 rows
                    // have no key) match by the localized family name.
                    foreach (var (familyKey, family, id) in candidates)
                    {
                        var isMatch = descriptor.FamilyKey is not null
                            ? string.Equals(familyKey, descriptor.FamilyKey, StringComparison.OrdinalIgnoreCase)
                            : string.Equals(family, descriptor.FamilyName, StringComparison.OrdinalIgnoreCase);
                        if (isMatch)
                        {
                            ids.Add(id);
                            matchedByDescriptor.Add((item, descriptor, id));
                            break;
                        }
                    }
                }
                else
                {
                    // Legacy row without family (pre-V26): first candidate,
                    // same as the pre-#183 name-only match.
                    ids.Add(candidates[0].Id);
                    matchedByDescriptor.Add((item, descriptor, candidates[0].Id));
                }
            }
            if (ids.Count > 0)
            {
                matched.Add((item, ids));
            }
            else
            {
                vanishedItemIds.Add(item.Id);
            }
        }

        if (vanishedItemIds.Count > 0)
        {
            lock (_cacheLock)
            {
                foreach (var id in vanishedItemIds)
                {
                    _systemTypeStaleByType.Remove(id);
                }
                if (_cachedSnapshot is not null)
                {
                    _cachedSnapshot = StaleSnapshotLogic.RemoveFrom(_cachedSnapshot, vanishedItemIds, _clock.UtcNow);
                }
            }
        }

        if (matched.Count == 0) return Array.Empty<StaleCheckResult>();

        var allIds = matched.SelectMany(m => m.TypeIds).Distinct().ToList();
        var markers = await _awaitable.RaiseAsync(
            _ => _systemTypeStore.ReadManyFromTypes(doc, allIds),
            ct).ConfigureAwait(true);

        var results = new List<StaleCheckResult>(matched.Count);
        foreach (var (item, typeIds) in matched)
        {
            var itemMarkers = typeIds
                .Select(id => markers.TryGetValue(id, out var m) ? m : null)
                .ToList();
            var (isStale, reason, loadedLabel) = AggregateSystemTypeMarkers(item, itemMarkers, targetRevit);
            results.Add(new StaleCheckResult(
                item.Id,
                item.Name,
                item.CurrentVersionLabel,
                loadedLabel,
                isStale,
                reason));
        }

        // #187: per-type stale map — one verdict per (family, name) type so
        // the tree can paint the ORANGE presence dot on the exact outdated
        // type, not just the leaf roll-up.
        lock (_cacheLock)
        {
            foreach (var group in matchedByDescriptor.GroupBy(m => m.Item.Id))
            {
                var item = group.First().Item;
                var typeMap = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var (_, descriptor, typeId) in group)
                {
                    markers.TryGetValue(typeId, out var marker);
                    // Stress test 2026-08-05 (semantics change): a missing
                    // marker is NOT stale — template-native types have
                    // unknown provenance, not proven outdatedness. Only a
                    // marker mismatch paints the orange dot.
                    var reason = marker is null
                        ? StaleReason.None
                        : SystemTypeStaleLogic.ComputeReason(
                            marker, item.Id, item.CurrentVersionLabel, targetRevit);
                    typeMap[BuildSystemTypeKey(descriptor.FamilyKey, descriptor.FamilyName, descriptor.Name)] = reason != StaleReason.None;
                }
                _systemTypeStaleByType[group.Key] = typeMap;
            }
        }

        return results;
    }

    public async Task<StaleCheckResult?> CheckSystemFamilyAsync(
        string catalogItemId,
        string displayName,
        Document doc,
        CancellationToken ct)
    {
        using var _scope = SmartConLogger.BeginScope(
            "StaleDetection",
            ("Method", nameof(CheckSystemFamilyAsync)),
            ("CatalogItemId", catalogItemId));

#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(doc);
#else
        if (doc is null) throw new ArgumentNullException(nameof(doc));
#endif

        var catalogItem = await _catalog.GetItemAsync(catalogItemId, ct).ConfigureAwait(false);
        if (catalogItem is null)
        {
            var notInCatalog = new StaleCheckResult(
                catalogItemId, displayName, null, null, true, StaleReason.NotInCatalog);
            MergeSingleResult(notInCatalog);
            return notInCatalog;
        }

        var descriptors = await _typeRepository
            .GetTypesForItemAsync(catalogItemId, ct)
            .ConfigureAwait(false);
        if (descriptors.Count == 0)
        {
            SmartConLogger.Info(
                $"CheckSystemFamily: '{displayName}' has no types in the catalog. " +
                "[Action: skipped — reimport the mini-project to rebuild the type list]");
            return null;
        }

        var categoryOrdinal = catalogItem.RevitCategoryId;
        var foundPairs = await _awaitable.RaiseAsync(
            _ =>
            {
                var pairs = new List<(FamilyTypeDescriptor Descriptor, ElementId TypeId)>();
                // #183: match by full identity (family, name) — with two
                // conduit families sharing "Стандарт" a name-only lookup
                // would return the SAME first match twice and never read
                // the marker of the other family's type. Legacy rows
                // (family NULL) degrade to the pre-#183 first-name match.
                foreach (var descriptor in descriptors)
                {
                    var id = _systemTypeFinder.FindTypeByName(
                        doc, descriptor.Name, categoryOrdinal, descriptor.FamilyName, descriptor.FamilyKey);
                    if (id is not null && !pairs.Any(p => p.TypeId == id))
                    {
                        pairs.Add((descriptor, id));
                    }
                }
                return pairs;
            }, ct).ConfigureAwait(true);

        if (foundPairs.Count == 0)
        {
            // Audit (B1): none of the item's types are in the project — the
            // family vanished; drop its snapshot entry and per-type map so
            // no phantom stale badge survives until InvalidateCache.
            lock (_cacheLock)
            {
                _systemTypeStaleByType.Remove(catalogItemId);
                if (_cachedSnapshot is not null)
                {
                    _cachedSnapshot = StaleSnapshotLogic.RemoveFrom(
                        _cachedSnapshot, new[] { catalogItemId }, _clock.UtcNow);
                }
            }
            SmartConLogger.Info(
                $"CheckSystemFamily: none of {descriptors.Count} types of '{displayName}' " +
                "are present in the project — snapshot entry dropped (not loaded). " +
                "[Action: load the types first]");
            return null;
        }

        var foundIds = foundPairs.Select(p => p.TypeId).ToList();
        var markers = await _awaitable.RaiseAsync(
            _ => _systemTypeStore.ReadManyFromTypes(doc, foundIds),
            ct).ConfigureAwait(true);

        var itemMarkers = foundIds
            .Select(id => markers.TryGetValue(id, out var m) ? m : null)
            .ToList();
        var targetRevit = ResolveTargetRevit();
        var (isStale, reason, loadedLabel) = AggregateSystemTypeMarkers(catalogItem, itemMarkers, targetRevit);

        // #187: per-type stale map (orange presence dot per exact type).
        var typeMap = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (descriptor, typeId) in foundPairs)
        {
            markers.TryGetValue(typeId, out var marker);
            var typeReason = marker is null
                ? StaleReason.None
                : SystemTypeStaleLogic.ComputeReason(
                    marker, catalogItemId, catalogItem.CurrentVersionLabel, targetRevit);
            typeMap[BuildSystemTypeKey(descriptor.FamilyKey, descriptor.FamilyName, descriptor.Name)] = typeReason != StaleReason.None;
        }
        lock (_cacheLock)
        {
            _systemTypeStaleByType[catalogItemId] = typeMap;
        }

        var result = new StaleCheckResult(
            catalogItemId,
            displayName,
            catalogItem.CurrentVersionLabel,
            loadedLabel,
            isStale,
            reason);
        MergeSingleResult(result);
        SmartConLogger.Info(
            $"CheckSystemFamily: '{displayName}' IsStale={isStale} Reason={reason} " +
            $"({foundIds.Count}/{descriptors.Count} types found in project).");
        return result;
    }

    /// <summary>
    /// Aggregates per-type markers of one system catalog item into a single
    /// verdict. Thin wrapper over the pure <see cref="SystemTypeStaleLogic"/>
    /// (Core) so the aggregation is unit-testable without Revit.
    /// </summary>
    internal static (bool IsStale, StaleReason Reason, string? LoadedLabel) AggregateSystemTypeMarkers(
        FamilyCatalogItem item,
        IReadOnlyList<FamilyVersion?> markers,
        int targetRevit)
    {
        return SystemTypeStaleLogic.Aggregate(
            markers, item.Id, item.CurrentVersionLabel, targetRevit);
    }

    private void MergeSingleResult(StaleCheckResult result)
    {
        lock (_cacheLock)
        {
            var before = _cachedSnapshot?.Results.Count ?? 0;
            _cachedSnapshot = StaleSnapshotLogic.MergeInto(_cachedSnapshot, new[] { result }, _clock.UtcNow);
            SmartConLogger.Info(
                $"MergeSingleResult: upserted entry for '{result.CatalogItemId}' " +
                $"(IsStale={result.IsStale}). " +
                $"Snapshot size: {before} -> {_cachedSnapshot.Results.Count}.");
        }
    }

    public FamilyStaleSnapshot? GetCachedSnapshot()
    {
        lock (_cacheLock) return _cachedSnapshot;
    }

    public FamilyStaleSnapshot GetMergedSnapshot(IReadOnlyList<StaleCheckResult> newResults)
    {
        lock (_cacheLock)
        {
            // #220: a null cache (cold start / all-empty checks / DB switch)
            // must not turn the apply path into a silent no-op — MergeInto
            // starts from the empty snapshot, so the post-DnD tree rebuild
            // always recomputes badges instead of keeping them frozen until
            // the next manual Check.
            return StaleSnapshotLogic.MergeInto(_cachedSnapshot, newResults, _clock.UtcNow);
        }
    }

    public void MarkUpdated(IReadOnlyCollection<string> catalogItemIds)
    {
        if (catalogItemIds is null || catalogItemIds.Count == 0) return;
        lock (_cacheLock)
        {
            foreach (var id in catalogItemIds)
            {
                _systemTypeStaleByType.Remove(id);
                _loadableTypeStaleByType.Remove(id);
            }
            if (_cachedSnapshot is null) return;
            var before = _cachedSnapshot.Results.Count;
            var updated = StaleSnapshotLogic.RemoveFrom(_cachedSnapshot, catalogItemIds, _clock.UtcNow);
            if (ReferenceEquals(updated, _cachedSnapshot))
            {
                SmartConLogger.Info(
                    $"MarkUpdated: none of {catalogItemIds.Count} IDs were in snapshot. " +
                    $"Snapshot size: {before} (unchanged).");
                return;
            }
            _cachedSnapshot = updated;
            SmartConLogger.Info(
                $"MarkUpdated: removed {before - _cachedSnapshot.Results.Count} entries. " +
                $"Snapshot size: {_cachedSnapshot.Results.Count}.");
        }
    }

    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cachedSnapshot = null;
            _systemTypeStaleByType.Clear();
            _loadableTypeStaleByType.Clear();
        }
    }

    public void InvalidateItems(IReadOnlyCollection<string> catalogItemIds)
    {
        if (catalogItemIds is null || catalogItemIds.Count == 0) return;
        lock (_cacheLock)
        {
            foreach (var id in catalogItemIds)
            {
                _systemTypeStaleByType.Remove(id);
                _loadableTypeStaleByType.Remove(id);
            }
            if (_cachedSnapshot is null) return;
            var before = _cachedSnapshot.Results.Count;
            _cachedSnapshot = StaleSnapshotLogic.RemoveFrom(_cachedSnapshot, catalogItemIds, _clock.UtcNow);
            SmartConLogger.Info(
                $"InvalidateItems: dropped {before - _cachedSnapshot.Results.Count} imported item(s) " +
                $"from the stale snapshot (re-check pending). " +
                $"Snapshot size: {_cachedSnapshot.Results.Count}.");
        }
    }

    /// <summary>
    /// #187: per-type stale verdicts of one system catalog item
    /// (typeKey "FAMILY|NAME" upper → isStale), or null when the item was
    /// never checked. Used by the tree to paint the orange presence dot on
    /// the exact outdated type node.
    /// </summary>
    public IReadOnlyDictionary<string, bool>? GetSystemTypeStaleMap(string catalogItemId)
    {
        lock (_cacheLock)
        {
            return _systemTypeStaleByType.TryGetValue(catalogItemId, out var map) ? map : null;
        }
    }

    /// <summary>
    /// #249 (Phase 2): per-type stale verdicts of one LOADABLE catalog
    /// item (typeName upper-invariant → isStale), or null when no
    /// per-type proof exists (never content-checked, indeterminate
    /// verification, or a family-level match). The tree falls back to
    /// the family-level (leaf-scoped) dot on null.
    /// </summary>
    public IReadOnlyDictionary<string, bool>? GetLoadableTypeStaleMap(string catalogItemId)
    {
        lock (_cacheLock)
        {
            return _loadableTypeStaleByType.TryGetValue(catalogItemId, out var map) ? map : null;
        }
    }

    /// <summary>
    /// #187: clears ONE type's stale verdict after its successful sync
    /// (per-type "Обновить") — the type's ES marker was just rewritten to the
    /// current catalog version, so its orange dot must clear immediately
    /// without a full "Проверить".
    /// </summary>
    public void MarkSystemTypeUpdated(string catalogItemId, string typeKey)
    {
        lock (_cacheLock)
        {
            if (_systemTypeStaleByType.TryGetValue(catalogItemId, out var map))
            {
                map[typeKey] = false;
            }
        }
    }

    /// <summary>
    /// #187: type key shared with the tree — "FAMILY|NAME" (upper-invariant),
    /// "|NAME" for legacy rows without family.
    /// #190 (ADR-064): the locale-invariant family_key is preferred over the
    /// localized family_name — both the map builder (descriptor side) and the
    /// tree lookup (node side) resolve the same effective token because the
    /// node is built from the same descriptor.
    /// </summary>
    internal static string BuildSystemTypeKey(string? familyKey, string? familyName, string typeName)
        => SystemTypeIdentityKey.Build(familyKey, familyName, typeName);

    private int ResolveTargetRevit()
    {
        string text;
        try
        {
            text = _revitContext.GetRevitVersion();
        }
        catch (Exception ex)
        {
            // IRevitContext throws InvalidOperationException if SetContext was
            // never called. The same defensiveness StaleFamilyUpdater applies.
            // Returning 0 means "unknown target" — ComputeReason will then
            // treat the family as not-stale on the version axis, which is the
            // conservative choice (we don't want to lie about staleness when
            // we genuinely don't know which Revit we are in).
            SmartConLogger.Warn(
                $"ResolveTargetRevit: IRevitContext.GetRevitVersion threw " +
                $"{ex.GetType().Name}: {ex.Message}. " +
                "[Action: report this warning — target Revit version is unknown " +
                "and StaleReason.RevitVersionMismatch will be skipped until the " +
                "context is initialised]");
            return 0;
        }
        if (int.TryParse(text, out var v)) return v;
        SmartConLogger.Warn(
            $"ResolveTargetRevit: IRevitContext.GetRevitVersion returned " +
            $"'{text}' which is not a valid year integer. " +
            "[Action: report this warning — RevitVersionMismatch will be skipped " +
            "for this session]");
        return 0;
    }
}
