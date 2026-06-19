using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Pure merge / prune logic for the stale-detection snapshot (ADR-030, Phase 24).
/// Kept in Core so unit tests can verify the snapshot semantics without spinning
/// up the Revit API (which would require the RevitAPI assembly).
/// </summary>
public static class StaleSnapshotLogic
{
    /// <summary>
    /// Merge <paramref name="newResults"/> into the existing snapshot. Existing
    /// entries for OTHER catalog item IDs are preserved. Entries that share a
    /// catalog item ID with the new results are overwritten (a re-check wins
    /// over a previous stale marker).
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="newResults"/> is null, or contains a null
    /// <see cref="StaleCheckResult"/>, or a null <c>CatalogItemId</c>.
    /// </exception>
    public static FamilyStaleSnapshot MergeInto(
        FamilyStaleSnapshot? existing,
        IReadOnlyList<StaleCheckResult> newResults,
        DateTimeOffset now)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(newResults);
#else
        if (newResults is null) throw new ArgumentNullException(nameof(newResults));
#endif

        var dict = new Dictionary<string, StaleCheckResult>(StringComparer.Ordinal);
        if (existing is not null)
        {
            foreach (var kvp in existing.Results) dict[kvp.Key] = kvp.Value;
        }
        foreach (var r in newResults)
        {
            if (r is null) throw new ArgumentException($"{nameof(newResults)} contains a null element", nameof(newResults));
            if (r.CatalogItemId is null) throw new ArgumentException($"{nameof(newResults)} contains a result with null CatalogItemId", nameof(newResults));
            dict[r.CatalogItemId] = r;
        }
        return new FamilyStaleSnapshot(dict, now);
    }

    /// <summary>
    /// Remove the given catalog item IDs from the existing snapshot. Returns the
    /// same snapshot instance if no entries matched (avoids allocation when the
    /// caller asked to remove IDs that were not in the snapshot).
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="catalogItemIds"/> is null, or contains a null element.
    /// </exception>
    public static FamilyStaleSnapshot RemoveFrom(
        FamilyStaleSnapshot? existing,
        IReadOnlyCollection<string> catalogItemIds,
        DateTimeOffset now)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(catalogItemIds);
#else
        if (catalogItemIds is null) throw new ArgumentNullException(nameof(catalogItemIds));
#endif

        if (existing is null) return FamilyStaleSnapshot.Empty;
        var dict = new Dictionary<string, StaleCheckResult>(StringComparer.Ordinal);
        foreach (var kvp in existing.Results) dict[kvp.Key] = kvp.Value;
        var removed = 0;
        foreach (var id in catalogItemIds)
        {
            if (id is null) throw new ArgumentException($"{nameof(catalogItemIds)} contains a null element", nameof(catalogItemIds));
            if (dict.Remove(id)) removed++;
        }
        if (removed == 0) return existing;
        return new FamilyStaleSnapshot(dict, now);
    }

    /// <summary>
    /// Return the subset of <paramref name="allStaleIds"/> that belongs to the
    /// given category subtree. Used by 'Update category' to scope the batch to
    /// the clicked category instead of every stale family in the snapshot.
    /// </summary>
    /// <param name="allStaleIds">All stale catalog item IDs in the snapshot.</param>
    /// <param name="categoryMap">
    /// Map produced by <c>IStaleCategoryAggregator.BuildCatalogToCategoryMap</c>:
    /// catalogItemId -> set of category IDs it belongs to (recursively expanded).
    /// </param>
    /// <param name="subtreeCategoryIds">
    /// Flat list of category IDs that make up the clicked subtree (the category
    /// itself + all descendants).
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Any argument is null, or <paramref name="categoryMap"/> contains a null
    /// key, or <paramref name="subtreeCategoryIds"/> contains a null element.
    /// </exception>
    public static IReadOnlyList<string> FilterStaleBySubtree(
        IReadOnlyList<string> allStaleIds,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> categoryMap,
        IReadOnlyCollection<string> subtreeCategoryIds)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(allStaleIds);
        ArgumentNullException.ThrowIfNull(categoryMap);
        ArgumentNullException.ThrowIfNull(subtreeCategoryIds);
#else
        if (allStaleIds is null) throw new ArgumentNullException(nameof(allStaleIds));
        if (categoryMap is null) throw new ArgumentNullException(nameof(categoryMap));
        if (subtreeCategoryIds is null) throw new ArgumentNullException(nameof(subtreeCategoryIds));
#endif

        // HashSet gives O(1) Contains lookups in the inner loop; the input is
        // IReadOnlyCollection<string> so we materialise it once. The cost is one
        // allocation per filter call - which happens once per Check/Update on
        // a category, not in a hot loop.
        var subtreeSet = new HashSet<string>(subtreeCategoryIds, StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var id in allStaleIds)
        {
            if (id is null) throw new ArgumentException($"{nameof(allStaleIds)} contains a null element", nameof(allStaleIds));
            if (categoryMap.TryGetValue(id, out var owners))
            {
                foreach (var o in owners)
                {
                    if (o is null) throw new ArgumentException($"{nameof(categoryMap)} contains a null category id for key '{id}'", nameof(categoryMap));
                    if (subtreeSet.Contains(o))
                    {
                        result.Add(id);
                        break;
                    }
                }
            }
        }
        return result;
    }
}
