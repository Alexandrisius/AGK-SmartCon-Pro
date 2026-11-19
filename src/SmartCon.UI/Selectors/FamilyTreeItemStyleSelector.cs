using System.Windows;
using System.Windows.Controls;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.UI.Selectors;

/// <summary>
/// Picks the TreeViewItem container style by the node-kind discriminator on
/// <see cref="ICatalogTreeNode"/> (category / family / type), so the selector lives
/// in SmartCon.UI without referencing concrete FamilyManager view-models.
/// </summary>
public class FamilyTreeItemStyleSelector : StyleSelector
{
    public Style? CategoryStyle { get; set; }
    public Style? FamilyStyle { get; set; }
    public Style? TypeStyle { get; set; }

    public override Style? SelectStyle(object item, DependencyObject container)
    {
        if (item is ICatalogTreeNode node)
        {
            if (node.IsCategory) return CategoryStyle;
            if (node.IsType) return TypeStyle;
            return FamilyStyle;
        }
        return base.SelectStyle(item, container);
    }
}
