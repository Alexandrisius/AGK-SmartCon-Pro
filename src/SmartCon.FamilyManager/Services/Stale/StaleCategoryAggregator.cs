using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.ViewModels;

namespace SmartCon.FamilyManager.Services.Stale;

/// <summary>
/// Pure aggregation logic for stale results across the category tree (ADR-030).
/// Used by <c>MainViewModel.ApplyStaleResultsToTreeAsync</c> to update
/// <c>CategoryNodeViewModel.HasStale</c> and <c>StaleCount</c> after a stale check.
/// </summary>
internal sealed class StaleCategoryAggregator : IStaleCategoryAggregator
{
    public IReadOnlyDictionary<string, bool> AggregateByCategory(
        IReadOnlyList<StaleCheckResult> results,
        IReadOnlyDictionary<string, IReadOnlyList<string>> categoryIndex)
    {
        var staleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in results)
        {
            if (r.IsStale) staleIds.Add(r.CatalogItemId);
        }

        var hasStale = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var kvp in categoryIndex)
        {
            var catalogItemId = kvp.Key;
            var categoryIds = kvp.Value;
            if (!staleIds.Contains(catalogItemId)) continue;
            foreach (var catId in categoryIds)
            {
                hasStale[catId] = true;
            }
        }
        return hasStale;
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
