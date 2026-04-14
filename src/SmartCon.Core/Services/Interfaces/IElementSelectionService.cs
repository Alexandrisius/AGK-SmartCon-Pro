using Autodesk.Revit.DB;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Interactive selection of MEP elements in Revit.
/// Filtering is an implementation detail (Revit layer). Core does not reference RevitAPIUI (I-09).
/// Implementation: SmartCon.Revit/Selection/ElementSelectionService.cs
/// </summary>
public interface IElementSelectionService
{
    /// <summary>
    /// Select one element with a free piping connector.
    /// Implementation applies ISelectionFilter on the Revit side.
    /// <paramref name="excludeElementId"/> — optionally excludes an element from selection (first pick when selecting second).
    /// Returns (ElementId, XYZ clickPoint) or null on ESC/cancel.
    /// </summary>
    (ElementId ElementId, XYZ ClickPoint)? PickElementWithFreeConnector(
        string statusMessage,
        ElementId? excludeElementId = null);
}
