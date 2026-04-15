using Autodesk.Revit.DB;
using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Traversal of connected MEP element chains (BFS via AllRefs).
/// Implementation: SmartCon.Revit/Selection/ElementChainIterator.cs
/// </summary>
public interface IElementChainIterator
{
    /// <summary>
    /// Build a ConnectionGraph starting from an element.
    /// stopAtElements — elements where BFS stops (not included in the graph).
    /// </summary>
    ConnectionGraph BuildGraph(Document doc, ElementId startElementId,
        HashSet<ElementId>? stopAtElements = null);

    /// <summary>
    /// Free connectors at chain boundaries (IsFree == true).
    /// </summary>
    IReadOnlyList<ConnectorProxy> GetChainEndConnectors(
        Document doc, ConnectionGraph graph);
}
