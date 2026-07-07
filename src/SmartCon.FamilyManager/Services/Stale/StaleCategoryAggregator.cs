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
        // Defense-in-depth: filter staleIds through results so a caller cannot
        // hand us a staleId for a result that is not in results (or vice versa).
        // Both inputs describe the same logical set; if they disagree, the
        // snapshot will end up inconsistent. Use results as the source of truth
        // for IsStale=true membership.
        var staleSet = new HashSet<string>(StringComparer.Ordinal);
        if (results is not null)
        {
            foreach (var r in results)
            {
                if (r is null) continue;
                if (r.IsStale && r.CatalogItemId is not null) staleSet.Add(r.CatalogItemId);
            }
        }
        if (staleIds is not null)
        {
            foreach (var id in staleIds)
            {
                if (id is not null) staleSet.Add(id);
            }
        }

        var perCategory = new Dictionary<string, CategoryStaleStats>(StringComparer.Ordinal);

        foreach (var catalogId in staleSet)
        {
            if (categoryMap is null) break;
            if (!categoryMap.TryGetValue(catalogId, out var categoryIds)) continue;
            if (categoryIds is null) continue;
            foreach (var catId in categoryIds)
            {
                if (catId is null) continue;
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
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(catalogItemIds);
        ArgumentNullException.ThrowIfNull(rootNodes);
#else
        if (catalogItemIds is null) throw new ArgumentNullException(nameof(catalogItemIds));
        if (rootNodes is null) throw new ArgumentNullException(nameof(rootNodes));
#endif

        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in catalogItemIds)
        {
            if (id is not null) wanted.Add(id);
        }
        var result = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
        foreach (var id in wanted) result[id] = new HashSet<string>(StringComparer.Ordinal);

        foreach (var root in rootNodes)
        {
            if (root is null) continue;
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
        if (node is null) return;
        var current = new List<string>(parentCategories) { node.CategoryId };

        if (node is ICategoryLeafProvider leafProvider)
        {
            var leaves = leafProvider.GetCatalogItemIds();
            if (leaves is not null)
            {
                foreach (var catalogId in leaves)
                {
                    if (catalogId is null) continue;
                    if (!wanted.Contains(catalogId)) continue;
                    if (!result.TryGetValue(catalogId, out var raw)) continue;
                    if (raw is not HashSet<string> set) continue;
                    foreach (var c in current)
                    {
                        if (c is not null) set.Add(c);
                    }
                }
            }
        }

        if (node.Children is null) return;
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
