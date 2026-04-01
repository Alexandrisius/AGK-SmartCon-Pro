using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;
using SmartCon.Revit.Extensions;

namespace SmartCon.Revit.Selection;

/// <summary>
/// ISelectionFilter: пропускает только элементы с хотя бы одним свободным трубопроводным коннектором.
/// Исключает ConnectorType.Curve (I-08). Фильтрует по Domain.DomainPiping.
/// Опционально исключает один элемент (например, уже выбранный первый элемент).
/// </summary>
public sealed class FreeConnectorFilter : ISelectionFilter
{
    private readonly ElementId? _excludeElementId;

    public FreeConnectorFilter(ElementId? excludeElementId = null)
    {
        _excludeElementId = excludeElementId;
    }

    public bool AllowElement(Element elem)
    {
        if (_excludeElementId is not null && elem.Id == _excludeElementId)
            return false;

        return elem.HasFreeConnectors();
    }

    public bool AllowReference(Reference reference, XYZ position)
    {
        return false;
    }
}
