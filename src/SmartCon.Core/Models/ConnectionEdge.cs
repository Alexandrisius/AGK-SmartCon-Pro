using Autodesk.Revit.DB;

namespace SmartCon.Core.Models;

/// <summary>
/// Ребро графа соединённых MEP-элементов — пара коннекторов между двумя элементами.
/// </summary>
public sealed record ConnectionEdge(
    ElementId FromElementId,
    int FromConnectorIndex,
    ElementId ToElementId,
    int ToConnectorIndex
);
