namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Per-category roll-up statistics for stale check results (ADR-030, Phase 24).
/// </summary>
/// <param name="HasStale">
/// <c>true</c> if any catalog item assigned to this category OR any of its
/// descendants is stale. The aggregator populates this field for every
/// category that appears in the per-item ancestor chain (built by
/// <c>StaleCategoryAggregator.BuildCatalogToCategoryMap</c>). A leaf with no
/// stale items has <c>HasStale = false</c>; a parent has <c>HasStale = true</c>
/// if any descendant is stale.
/// </param>
/// <param name="StaleCount">
/// Number of <b>distinct</b> stale catalog items counted in this category's
/// ancestor chain. Because the aggregator increments the count for every
/// ancestor of every stale item, a category that directly contains 5 stale
/// items shows <c>StaleCount = 5</c>. Each of those 5 items also gets
/// <c>StaleCount = 1</c> on its own leaf node (because the leaf IS an
/// ancestor of itself). The number is a count of stale items in the
/// category's subtree, not "items directly attached".
/// <para>
///     The tooltip converter renders this as a localised "N stale families"
///     string. With the recursive semantics, a parent with 5 stale leaves
///     and the 5 leaves each show "5" and "1" respectively — the parent
///     reports the size of the entire stale subtree, the leaves report
///     their own one-item contribution. This matches what operators expect
///     from the Project Browser: "this category has 5 stale items total,
///     and here is each one".
/// </para>
/// </param>
public sealed record CategoryStaleStats(bool HasStale, int StaleCount)
{
    public static CategoryStaleStats Empty { get; } = new(false, 0);
}
