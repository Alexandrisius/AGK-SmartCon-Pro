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
    private FamilyStaleSnapshot? _cachedSnapshot;
    private readonly object _cacheLock = new();

    public StaleDetector(
        IFamilyVersionStore store,
        IFamilyCatalogProvider catalog,
        IFamilyManagerAwaitableEvent awaitable,
        IRevitContext revitContext,
        IClock clock)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(awaitable);
        ArgumentNullException.ThrowIfNull(revitContext);
        ArgumentNullException.ThrowIfNull(clock);
#else
        if (store is null) throw new ArgumentNullException(nameof(store));
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));
        if (awaitable is null) throw new ArgumentNullException(nameof(awaitable));
        if (revitContext is null) throw new ArgumentNullException(nameof(revitContext));
        if (clock is null) throw new ArgumentNullException(nameof(clock));
#endif
        _store = store;
        _catalog = catalog;
        _awaitable = awaitable;
        _revitContext = revitContext;
        _clock = clock;
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

            if (loaded is null)
            {
                result = new StaleCheckResult(
                    catalogItemId, familyName,
                    catalogItem.CurrentVersionLabel, null,
                    IsStale: true, Reason: StaleReason.NoEntityStorage);
            }
            else
            {
                var targetRevit = ResolveTargetRevit();
                var reason = ComputeReason(loaded, catalogItem, targetRevit);
                result = new StaleCheckResult(
                    catalogItemId, familyName,
                    catalogItem.CurrentVersionLabel, loaded.VersionLabel,
                    IsStale: reason != StaleReason.None, Reason: reason);
            }
        }

        // Single-family check updates the snapshot entry for this family only,
        // leaving all other entries intact.
        lock (_cacheLock)
        {
            var before = _cachedSnapshot?.Results.Count ?? 0;
            _cachedSnapshot = StaleSnapshotLogic.MergeInto(_cachedSnapshot, new[] { result }, _clock.UtcNow);
            SmartConLogger.Info(
                $"CheckFamily: upserted entry for '{result.CatalogItemId}' " +
                $"(IsStale={result.IsStale}). " +
                $"Snapshot size: {before} -> {_cachedSnapshot.Results.Count}.");
        }
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
            ManufacturerFilter: null,
            Sort: FamilyCatalogSort.NameAsc,
            Offset: 0,
            Limit: int.MaxValue,
            IncludeUncategorized: hasUncategorized,
            CategoryIdsFilter: hasRealFilter ? realCategoryIds : null);
        var catalogItems = await _catalog.SearchAsync(query, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (catalogItems.Count == 0) return Array.Empty<StaleCheckResult>();

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
        foreach (var item in catalogItems)
        {
            if (matchCounter.ShouldLog())
            {
                SmartConLogger.Debug(
                    $"Matching {matchCounter.Count}/{catalogItems.Count}: '{item.Name}'.");
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
        var targetRevit = ResolveTargetRevit();
        var reasonCounter = new HotLoopCounter(sampleEvery: 32);
        foreach (var (item, familyName, id) in matched)
        {
            versions.TryGetValue(id, out var loaded);
            StaleReason reason;
            if (loaded is null)
            {
                reason = StaleReason.NoEntityStorage;
            }
            else
            {
                reason = ComputeReason(loaded, item, targetRevit);
            }

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

    public FamilyStaleSnapshot? GetCachedSnapshot()
    {
        lock (_cacheLock) return _cachedSnapshot;
    }

    public FamilyStaleSnapshot? GetMergedSnapshot(IReadOnlyList<StaleCheckResult> newResults)
    {
        lock (_cacheLock)
        {
            if (_cachedSnapshot is null) return null;
            return StaleSnapshotLogic.MergeInto(_cachedSnapshot, newResults, _clock.UtcNow);
        }
    }

    public void MarkUpdated(IReadOnlyCollection<string> catalogItemIds)
    {
        if (catalogItemIds is null || catalogItemIds.Count == 0) return;
        lock (_cacheLock)
        {
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
        lock (_cacheLock) _cachedSnapshot = null;
    }

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

    private static StaleReason ComputeReason(FamilyVersion loaded, FamilyCatalogItem item, int targetRevit)
    {
        // Defensive: a marker for a different catalog ID would yield a false
        // "not stale" verdict. This is rare (would require manually-written ES
        // data with the wrong GUID) but cheap to guard.
        if (!string.IsNullOrEmpty(loaded.CatalogItemId) &&
            !string.Equals(loaded.CatalogItemId, item.Id, StringComparison.Ordinal))
        {
            SmartConLogger.Warn(
                $"ComputeReason: ES marker CatalogItemId='{loaded.CatalogItemId}' " +
                $"does not match catalog id '{item.Id}'. " +
                "[Action: ES data is corrupted for this family; treat as stale]");
            return StaleReason.VersionMismatch;
        }

        if (!string.IsNullOrEmpty(item.CurrentVersionLabel) &&
            !string.Equals(loaded.VersionLabel, item.CurrentVersionLabel, StringComparison.Ordinal))
        {
            return StaleReason.VersionMismatch;
        }
        if (loaded.SourceRevitVersion > 0 && targetRevit > 0 &&
            loaded.SourceRevitVersion != targetRevit)
        {
            return StaleReason.RevitVersionMismatch;
        }
        return StaleReason.None;
    }
}
