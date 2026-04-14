using Autodesk.Revit.DB;

namespace SmartCon.Core.Models;

/// <summary>
/// Edge of the connected MEP elements graph — a pair of connectors between two elements.
/// </summary>
public sealed record ConnectionEdge(
    ElementId FromElementId,
    int FromConnectorIndex,
    ElementId ToElementId,
    int ToConnectorIndex
);
