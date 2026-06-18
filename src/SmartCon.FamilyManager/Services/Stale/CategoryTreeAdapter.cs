using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.ViewModels;

namespace SmartCon.FamilyManager.Services.Stale;

/// <summary>
/// Adapter that exposes a <see cref="CategoryNodeViewModel"/> tree to Core
/// (which has no UI types — I-09). Implements <see cref="ICategoryNodeInfo"/>
/// and, when the node has direct family leaves, <see cref="ICategoryLeafProvider"/>.
/// </summary>
internal sealed class CategoryTreeAdapter : ICategoryNodeInfo, ICategoryLeafProvider
{
    private readonly CategoryNodeViewModel _category;
    private readonly IReadOnlyList<ICategoryNodeInfo> _childCategories;
    private readonly IReadOnlyList<string> _leafCatalogItemIds;

    public CategoryTreeAdapter(CategoryNodeViewModel category)
    {
        _category = category;

        var childCategories = new List<ICategoryNodeInfo>();
        var leafIds = new List<string>();
        foreach (var child in category.Children)
        {
            if (child is CategoryNodeViewModel sub)
            {
                childCategories.Add(new CategoryTreeAdapter(sub));
            }
            else if (child is FamilyLeafNodeViewModel leaf && !string.IsNullOrEmpty(leaf.CatalogItemId))
            {
                leafIds.Add(leaf.CatalogItemId);
            }
        }
        _childCategories = childCategories;
        _leafCatalogItemIds = leafIds;
    }

    public string CategoryId => _category.CategoryId;

    public IReadOnlyList<ICategoryNodeInfo> Children => _childCategories;

    public IEnumerable<string> GetCatalogItemIds() => _leafCatalogItemIds;

    /// <summary>
    /// Build the full adapter tree for a collection of <see cref="CategoryNodeViewModel"/> roots.
    /// </summary>
    public static IReadOnlyList<ICategoryNodeInfo> AdaptRoots(
        IEnumerable<CategoryNodeViewModel> rootCategories)
    {
        var list = new List<ICategoryNodeInfo>();
        foreach (var c in rootCategories)
        {
            list.Add(new CategoryTreeAdapter(c));
        }
        return list;
    }
}
