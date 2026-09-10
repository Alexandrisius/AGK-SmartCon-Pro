using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
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
internal sealed partial class StaleDetector : IStaleDetector
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
    private readonly IFamilyRoutingRuleRepository? _routingRuleRepository;
    private readonly ISegmentRuleRepository? _segmentRuleRepository;
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
        IContentHashAnalyticsRepository? contentHashAnalytics = null,
        IFamilyRoutingRuleRepository? routingRuleRepository = null,
        ISegmentRuleRepository? segmentRuleRepository = null)
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
        _routingRuleRepository = routingRuleRepository;
        _segmentRuleRepository = segmentRuleRepository;
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
        CancellationToken ct,
        IProgress<StaleCheckProgress>? progress = null)
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

        var loadableDone = 0;
        if (loadableItems.Count > 0)
        {
            var loadableResults = await CheckLoadableItemsAsync(
                    loadableItems, doc, targetRevit, progress, systemItems.Count, ct)
                .ConfigureAwait(true);
            results.AddRange(loadableResults);
            loadableDone = loadableResults.Count;
        }

        if (systemItems.Count > 0)
        {
            var systemResults = await CheckSystemItemsAsync(
                    systemItems, doc, targetRevit, progress, loadableDone, ct)
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
        => SystemTypeIdentityKey.Build(
            string.IsNullOrEmpty(familyKey) ? null : familyKey,
            string.IsNullOrEmpty(familyName) ? null : familyName,
            typeName);

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
