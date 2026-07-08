using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// Sidebar filter category for the Content tab. <see cref="All"/> shows every
/// asset in a flat list; the other values filter by <see cref="FamilyAssetType"/>.
/// <see cref="Table"/> combines LookupTable and Spreadsheet. Model3D is not
/// shown as a separate category (auto-extracted GLB are filtered out, and
/// user-added 3D files appear under Other).
/// </summary>
public enum ContentCategory
{
    All = -1,
    Image = 0,
    Video = 1,
    Document = 2,
    Table = 100,
    Other = 5
}

/// <summary>
/// Sidebar navigation item for the Content tab. Pairs a category with its
/// localized display name, icon resource key, and live file count.
/// </summary>
public sealed class CategoryNavItem
{
    public ContentCategory Category { get; }
    public FamilyAssetType? AssetType { get; }
    public string DisplayName { get; }
    public string IconResourceKey { get; }
    public int Count { get; set; }

    public CategoryNavItem(ContentCategory category, FamilyAssetType? assetType,
        string displayName, string iconResourceKey, int count = 0)
    {
        Category = category;
        AssetType = assetType;
        DisplayName = displayName;
        IconResourceKey = iconResourceKey;
        Count = count;
    }

    public override string ToString() => DisplayName + " (" + Count + ")";
}
