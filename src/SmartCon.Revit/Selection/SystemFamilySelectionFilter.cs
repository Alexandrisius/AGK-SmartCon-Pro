using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;
using SmartCon.Core.Compatibility;
using SmartCon.Revit.FamilyManager;

namespace SmartCon.Revit.Selection;

internal sealed class SystemFamilySelectionFilter : ISelectionFilter
{
    public bool AllowElement(Element elem)
    {
        var bic = CategoryCompat.GetBuiltInCategory(elem.Category);
        if (bic == BuiltInCategory.INVALID) return false;
        return SystemCategoryRegistry.SupportedCategories.Contains(bic);
    }

    public bool AllowReference(Reference reference, XYZ position) => false;
}
