using Autodesk.Revit.DB;

namespace SmartCon.Core.Models;

/// <summary>
/// Record of a connection between two connectors.
/// Stored in ConnectionGraph during BuildGraph for later restoration on rollback (minus).
/// </summary>
public sealed record ConnectionRecord(
    ElementId ThisElementId,
    int ThisConnectorIndex,
    ElementId NeighborElementId,
    int NeighborConnectorIndex
);
