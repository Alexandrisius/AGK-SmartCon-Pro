using System.Windows;
using System.Windows.Controls;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.UI.Selectors;

/// <summary>
/// Picks the item DataTemplate for a catalog tree node by the kind discriminator on
/// <see cref="ICatalogTreeNode"/> (category / family / type). Lets the FamilyManager
/// panel use keyed templates selected without local-type XAML references (no WPF
/// MarkupCompilePass2 / _wpftmp double-compile).
/// </summary>
public sealed class CatalogTreeNodeTemplateSelector : DataTemplateSelector
{
    public DataTemplate? CategoryTemplate { get; set; }
    public DataTemplate? FamilyTemplate { get; set; }
    public DataTemplate? TypeTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
    {
        if (item is ICatalogTreeNode node)
        {
            if (node.IsCategory) return CategoryTemplate;
            if (node.IsType) return TypeTemplate;
            return FamilyTemplate;
        }
        return base.SelectTemplate(item, container);
    }
}