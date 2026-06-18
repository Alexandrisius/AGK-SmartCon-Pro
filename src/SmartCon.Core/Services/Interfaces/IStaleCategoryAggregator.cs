using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Pure aggregation logic for stale results across the category tree (ADR-030).
/// Used by <c>MainViewModel.ApplyStaleResultsToTreeAsync</c> to update
/// <c>CategoryNodeViewModel.HasStale</c> and <c>StaleCount</c> after a stale check.
/// </summary>
public interface IStaleCategoryAggregator
{
    /// <summary>
    /// Roll-up: for each category in <paramref name="categoryMap"/>, compute
    /// <see cref="CategoryStaleStats"/> (HasStale + StaleCount).
    /// Single pass: O(n + m) where n = number of stale items, m = number of
    /// category memberships.
    /// </summary>
    /// <param name="results">Stale check results from the detector.</param>
    /// <param name="categoryMap">
    /// Maps catalog item ID to the collection of category IDs it belongs to
    /// (with recursive parent expansion).
    /// </param>
    /// <param name="staleIds">
    /// Pre-computed set of stale catalog item IDs (the caller already builds this
    /// for the leaf walk; the aggregator reuses it to avoid a second scan).
    /// </param>
    IReadOnlyDictionary<string, CategoryStaleStats> AggregateByCategory(
        IReadOnlyList<StaleCheckResult> results,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> categoryMap,
        IReadOnlyCollection<string> staleIds);

    /// <summary>
    /// Build a reverse map: for each catalog item ID, the collection of category IDs it
    /// belongs to (recursively expanded). Used by the VM to update
    /// <c>HasStale</c> on the category tree in one pass.
    /// </summary>
    /// <param name="catalogItemIds">Catalog items to map.</param>
    /// <param name="rootNodes">Top-level nodes of the category tree.</param>
    IReadOnlyDictionary<string, IReadOnlyCollection<string>> BuildCatalogToCategoryMap(
        IEnumerable<string> catalogItemIds,
        IEnumerable<ICategoryNodeInfo> rootNodes);
}

/// <summary>
/// Abstraction over <c>CategoryNodeViewModel</c> used by
/// <see cref="IStaleCategoryAggregator.BuildCatalogToCategoryMap"/>
/// to keep Core free of UI dependencies (I-09).
/// Implementations live in <c>SmartCon.FamilyManager</c> (adapters on top of <c>CategoryNodeViewModel</c>).
/// </summary>
public interface ICategoryNodeInfo
{
    string CategoryId { get; }

    /// <summary>Children of this category (may include both sub-categories and leaves).</summary>
    IReadOnlyList<ICategoryNodeInfo> Children { get; }
}
