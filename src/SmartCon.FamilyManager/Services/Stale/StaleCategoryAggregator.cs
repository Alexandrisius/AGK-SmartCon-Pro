using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.Stale;

/// <summary>
/// Pure aggregation logic for stale results across the category tree (ADR-030).
/// Used by <c>MainViewModel.ApplyStaleResultsToTreeAsync</c> to update
/// <c>CategoryNodeViewModel.HasStale</c> and <c>StaleCount</c> after a stale check.
/// </summary>
internal sealed class StaleCategoryAggregator : IStaleCategoryAggregator
{
    public IReadOnlyDictionary<string, CategoryStaleStats> AggregateByCategory(
        IReadOnlyList<StaleCheckResult> results,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> categoryMap,
        IReadOnlyCollection<string> staleIds)
    {
        var staleSet = staleIds as IReadOnlyCollection<string> is null
            ? new HashSet<string>(staleIds, StringComparer.Ordinal)
            : new HashSet<string>(staleIds, StringComparer.Ordinal);

        var perCategory = new Dictionary<string, CategoryStaleStats>(StringComparer.Ordinal);

        foreach (var catalogId in staleSet)
        {
            if (!categoryMap.TryGetValue(catalogId, out var categoryIds)) continue;
            foreach (var catId in categoryIds)
            {
                var current = perCategory.TryGetValue(catId, out var s)
                    ? s
                    : CategoryStaleStats.Empty;
                perCategory[catId] = new CategoryStaleStats(
                    HasStale: true,
                    StaleCount: current.StaleCount + 1);
            }
        }
        return perCategory;
    }

    public IReadOnlyDictionary<string, IReadOnlyCollection<string>> BuildCatalogToCategoryMap(
        IEnumerable<string> catalogItemIds,
        IEnumerable<ICategoryNodeInfo> rootNodes)
    {
        var wanted = new HashSet<string>(catalogItemIds, StringComparer.Ordinal);
        var result = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
        foreach (var id in wanted) result[id] = new HashSet<string>(StringComparer.Ordinal);

        foreach (var root in rootNodes)
        {
            Accumulate(root, [], wanted, result);
        }
        return result;
    }

    private static void Accumulate(
        ICategoryNodeInfo node,
        IReadOnlyList<string> parentCategories,
        HashSet<string> wanted,
        Dictionary<string, IReadOnlyCollection<string>> result)
    {
        var current = new List<string>(parentCategories) { node.CategoryId };

        if (node is ICategoryLeafProvider leafProvider)
        {
            foreach (var catalogId in leafProvider.GetCatalogItemIds())
            {
                if (!wanted.Contains(catalogId)) continue;
                var set = (HashSet<string>)result[catalogId];
                foreach (var c in current) set.Add(c);
            }
        }

        foreach (var child in node.Children)
        {
            Accumulate(child, current, wanted, result);
        }
    }
}

/// <summary>
/// Optional second interface on a category node that exposes the catalog items
/// directly attached to it (i.e. its <c>FamilyLeafNodeViewModel</c> children).
/// <see cref="CategoryNodeViewModel"/> does not implement this directly because
/// category leaves live in <c>Children</c> as <c>CatalogTreeNodeViewModel</c>
/// (sub-categories + family leaves). The adapter in this module maps the
/// VM hierarchy to <see cref="ICategoryNodeInfo"/> + <see cref="ICategoryLeafProvider"/>.
/// </summary>
internal interface ICategoryLeafProvider
{
    IEnumerable<string> GetCatalogItemIds();
}
