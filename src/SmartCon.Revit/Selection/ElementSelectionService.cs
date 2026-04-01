using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Context;

namespace SmartCon.Revit.Selection;

/// <summary>
/// Реализация IElementSelectionService через Revit PickObject API.
/// Использует IRevitUIContext (Revit-слой) для доступа к UIDocument.
/// </summary>
public sealed class ElementSelectionService : IElementSelectionService
{
    private readonly IRevitUIContext _uiContext;

    public ElementSelectionService(IRevitUIContext uiContext)
    {
        _uiContext = uiContext;
    }

    /// <inheritdoc />
    public (ElementId ElementId, XYZ ClickPoint)? PickElementWithFreeConnector(
        string statusMessage,
        ElementId? excludeElementId = null)
    {
        var uiDoc = _uiContext.GetUIDocument();

        try
        {
            var reference = uiDoc.Selection.PickObject(
                ObjectType.Element,
                new FreeConnectorFilter(excludeElementId),
                statusMessage);

            var elementId = reference.ElementId;
            var clickPoint = reference.GlobalPoint;

            return (elementId, clickPoint);
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return null;
        }
    }
}
