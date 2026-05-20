using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;

namespace SmartCon.Revit.Selection;

internal sealed class PipeSelectionFilter : ISelectionFilter
{
    public bool AllowElement(Element elem)
    {
        {
#if REVIT2024_OR_GREATER
            return elem.Category?.Id.Value == (long)BuiltInCategory.OST_PipeCurves;
#else
            return elem.Category?.Id.IntegerValue == (int)BuiltInCategory.OST_PipeCurves;
#endif
        }
    }

    public bool AllowReference(Reference reference, XYZ position)
    {
        return false;
    }
}
