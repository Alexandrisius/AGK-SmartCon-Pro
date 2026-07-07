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
    /// </summary>
    /// <param name="results">
    /// Stale check results from the detector. The aggregator uses both
    /// <paramref name="results"/> (filtered to <c>IsStale=true</c>) and
    /// <paramref name="staleIds"/> (pre-computed set) as inputs. Defence in
    /// depth: the two are unioned so a caller cannot accidentally hand us
    /// inconsistent data and silently lose entries.
    /// </param>
    /// <param name="categoryMap">
    /// Maps catalog item ID to the collection of category IDs it belongs to
    /// (with recursive parent expansion). Produced by
    /// <see cref="BuildCatalogToCategoryMap"/>.
    /// </param>
    /// <param name="staleIds">
    /// Pre-computed set of stale catalog item IDs (the caller already builds this
    /// for the leaf walk; the aggregator reuses it to avoid a second scan).
    /// May be <c>null</c> or empty — the aggregator still iterates
    /// <paramref name="results"/>.
    /// </param>
    /// <remarks>
    /// <b>Recursive semantics:</b> a stale leaf increments the count for
    /// every ancestor category (built by
    /// <see cref="BuildCatalogToCategoryMap"/>). A category that directly
    /// contains 5 stale items shows <c>StaleCount = 5</c>. Each of those
    /// 5 leaves shows <c>StaleCount = 1</c> on its own node (the leaf IS
    /// an ancestor of itself). See <see cref="CategoryStaleStats.StaleCount"/>
    /// for the UX rationale.
    /// </remarks>
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
    /// <param name="rootNodes">Top-level nodes of the category tree. Cyclic
    /// trees will cause a <see cref="StackOverflowException"/>; the caller
    /// must guarantee the tree is a DAG.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="catalogItemIds"/> or <paramref name="rootNodes"/> is null.
    /// </exception>
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
