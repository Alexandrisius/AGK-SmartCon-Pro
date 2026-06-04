using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;
using SmartCon.Core.Compatibility;

namespace SmartCon.Revit.Selection;

internal sealed class SystemFamilySelectionFilter : ISelectionFilter
{
    private static readonly HashSet<BuiltInCategory> SupportedCategories = new HashSet<BuiltInCategory>
    {
        BuiltInCategory.OST_PipeCurves,
        BuiltInCategory.OST_DuctCurves,
        BuiltInCategory.OST_FlexPipeCurves,
        BuiltInCategory.OST_FlexDuctCurves,
        BuiltInCategory.OST_Conduit,
        BuiltInCategory.OST_CableTray,
        BuiltInCategory.OST_DuctInsulations,
        BuiltInCategory.OST_PipeInsulations,
        BuiltInCategory.OST_Walls,
        BuiltInCategory.OST_Floors,
        BuiltInCategory.OST_Roofs,
        BuiltInCategory.OST_Ceilings,
        BuiltInCategory.OST_Stairs,
        BuiltInCategory.OST_Railings,
    };

    public bool AllowElement(Element elem)
    {
        if (elem.Category?.Id is not ElementId catId) return false;
        var bic = (BuiltInCategory)(int)catId.GetValue();
        return SupportedCategories.Contains(bic);
    }

    public bool AllowReference(Reference reference, XYZ position) => false;
}
