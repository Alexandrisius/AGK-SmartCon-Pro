using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.Stale;

/// <summary>
/// On-demand stale detector (ADR-030, Issue #69). Reads <see cref="FamilyVersion"/>
/// markers from the active project via <see cref="IFamilyVersionStore"/> and compares
/// them with the catalog. Maintains a session-scoped cache invalidated on
/// Load/Update/Edit/DB-switch (D-10).
/// </summary>
internal sealed class StaleDetector : IStaleDetector
{
    private readonly IFamilyVersionStore _store;
    private readonly IFamilyCatalogProvider _catalog;
    private readonly IFamilyManagerAwaitableEvent _awaitable;
    private readonly IClock _clock;
    private FamilyStaleSnapshot? _cachedSnapshot;
    private readonly object _cacheLock = new();

    public StaleDetector(
        IFamilyVersionStore store,
        IFamilyCatalogProvider catalog,
        IFamilyManagerAwaitableEvent awaitable,
        IClock clock)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(awaitable);
        ArgumentNullException.ThrowIfNull(clock);
#else
        if (store is null) throw new ArgumentNullException(nameof(store));
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));
        if (awaitable is null) throw new ArgumentNullException(nameof(awaitable));
        if (clock is null) throw new ArgumentNullException(nameof(clock));
#endif
        _store = store;
        _catalog = catalog;
        _awaitable = awaitable;
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
        if (catalogItem is null)
        {
            return new StaleCheckResult(catalogItemId, familyName, null, null, true, StaleReason.NotInCatalog);
        }

        // ES read must run on Revit main thread (I-01). Use RaiseAsync to marshal.
        var loaded = await _awaitable.RaiseAsync(
            _ => _store.ReadFromLoadedFamily(doc, familyId),
            ct).ConfigureAwait(true);

        if (loaded is null)
        {
            return new StaleCheckResult(
                catalogItemId, familyName,
                catalogItem.CurrentVersionLabel, null,
                IsStale: true, Reason: StaleReason.NoEntityStorage);
        }

        var reason = ComputeReason(loaded, catalogItem);
        var isStale = reason != StaleReason.None;

        return new StaleCheckResult(
            catalogItemId, familyName,
            catalogItem.CurrentVersionLabel, loaded.VersionLabel,
            isStale, reason);
    }

    public async Task<IReadOnlyList<StaleCheckResult>> CheckCategoryAsync(
        string? categoryId,
        bool recursive,
        Document doc,
        CancellationToken ct)
    {
        using var _scope = SmartConLogger.BeginScope(
            "StaleDetection",
            ("Method", nameof(CheckCategoryAsync)),
            ("CategoryId", categoryId ?? string.Empty));

#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(doc);
#else
        if (doc is null) throw new ArgumentNullException(nameof(doc));
#endif

        // 1) Read catalog items for the category (SQLite, async, no Revit API).
        // "__no_category__" is the synthetic ID for the uncategorized node;
        // the actual SQL filter must match NULL/empty category_id.
        var includeUncategorized = categoryId == "__no_category__";
        var query = new FamilyCatalogQuery(
            SearchText: null,
            CategoryFilter: includeUncategorized ? null : categoryId,
            StatusFilter: null,
            Tags: null,
            ManufacturerFilter: null,
            Sort: FamilyCatalogSort.NameAsc,
            Offset: 0,
            Limit: int.MaxValue,
            IncludeUncategorized: includeUncategorized);
        var catalogItems = await _catalog.SearchAsync(query, ct).ConfigureAwait(false);
        if (catalogItems.Count == 0) return Array.Empty<StaleCheckResult>();

        // 2) Collect Family element ids from the active document on the Revit thread.
        var familyIds = await _awaitable.RaiseAsync(
            _ =>
            {
                var map = new Dictionary<string, (string FamilyName, ElementId Id)>(StringComparer.Ordinal);
                using var collector = new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Family));
                foreach (Autodesk.Revit.DB.Family f in collector)
                {
                    if (f is null || f.Name is null) continue;
                    map[f.Name] = (f.Name, f.Id);
                }
                return map;
            }, ct).ConfigureAwait(true);

        // 3) Match catalog items to Revit Family elements by name (left join).
        var matched = new List<(FamilyCatalogItem Item, string FamilyName, ElementId Id)>();
        foreach (var item in catalogItems)
        {
            if (familyIds.TryGetValue(item.Name, out var hit))
            {
                matched.Add((item, hit.FamilyName, hit.Id));
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
                reason = ComputeReason(loaded, item);
            }

            results.Add(new StaleCheckResult(
                item.Id, familyName,
                item.CurrentVersionLabel,
                loaded?.VersionLabel,
                reason != StaleReason.None,
                reason));
        }

        // 6) Update session cache.
        var snapshot = new FamilyStaleSnapshot(
            results.ToDictionary(r => r.CatalogItemId, StringComparer.Ordinal),
            _clock.UtcNow);
        lock (_cacheLock) _cachedSnapshot = snapshot;

        return results;
    }

    public FamilyStaleSnapshot? GetCachedSnapshot()
    {
        lock (_cacheLock) return _cachedSnapshot;
    }

    public void InvalidateCache()
    {
        lock (_cacheLock) _cachedSnapshot = null;
    }

    private static StaleReason ComputeReason(FamilyVersion loaded, FamilyCatalogItem item)
    {
        if (!string.IsNullOrEmpty(item.CurrentVersionLabel) &&
            !string.Equals(loaded.VersionLabel, item.CurrentVersionLabel, StringComparison.Ordinal))
        {
            return StaleReason.VersionMismatch;
        }
        return StaleReason.None;
    }
}
