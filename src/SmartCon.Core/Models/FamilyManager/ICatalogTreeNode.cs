namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Presentation contract for a catalog tree node (category / family / type).
/// Lets SmartCon.UI tree selectors and the sticky-header behavior work against
/// an abstraction instead of concrete FamilyManager view-models, so the panel
/// XAML references only external types (no WPF local-type MarkupCompilePass2 /
/// _wpftmp double-compile). Implemented by CatalogTreeNodeViewModel.
/// </summary>
public interface ICatalogTreeNode
{
    string DisplayName { get; }

    bool IsCategory { get; }

    bool IsType { get; }

    ICatalogTreeNode? Parent { get; }

    IEnumerable<ICatalogTreeNode> Children { get; }
}

/// <summary>A category node (carries a family-count badge).</summary>
public interface ICategoryNode : ICatalogTreeNode
{
    int FamilyCount { get; }
}