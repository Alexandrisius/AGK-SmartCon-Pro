using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;
using SmartCon.Revit.Extensions;

namespace SmartCon.Revit.Selection;

/// <summary>
/// ISelectionFilter: пропускает только элементы с хотя бы одним свободным коннектором.
/// Исключает ConnectorType.Curve (I-08).
/// </summary>
public sealed class FreeConnectorFilter : ISelectionFilter
{
    public bool AllowElement(Element elem)
    {
        return elem.HasFreeConnectors();
    }

    public bool AllowReference(Reference reference, XYZ position)
    {
        return false;
    }
}
