using Autodesk.Revit.DB;

namespace SmartCon.Core.Models;

/// <summary>
/// Запись о соединении между двумя коннекторами.
/// Сохраняется в ConnectionGraph при BuildGraph для последующего восстановления при откате (−).
/// </summary>
public sealed record ConnectionRecord(
    ElementId ThisElementId,
    int ThisConnectorIndex,
    ElementId NeighborElementId,
    int NeighborConnectorIndex
);
