using System.Windows;
using System.Windows.Controls;
using SmartCon.FamilyManager.ViewModels;

namespace SmartCon.FamilyManager.Selectors;

public class FamilyTreeItemStyleSelector : StyleSelector
{
    public Style? CategoryStyle { get; set; }
    public Style? FamilyStyle { get; set; }
    public Style? TypeStyle { get; set; }

    public override Style? SelectStyle(object item, DependencyObject container)
    {
        return item switch
        {
            CategoryNodeViewModel => CategoryStyle,
            FamilyLeafNodeViewModel => FamilyStyle,
            FamilyTypeNodeViewModel => TypeStyle,
            _ => base.SelectStyle(item, container)
        };
    }
}
