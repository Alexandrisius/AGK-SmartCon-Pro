using Autodesk.Revit.DB;

namespace SmartCon.Core.Models;

/// <summary>
/// One entry of the flattened chain element queue: the element itself and the
/// BFS level it was discovered at. The level is required to locate the parent
/// edge (parents always live on level − 1) and for level-boundary detection
/// during early chain sealing (ADR-052).
/// </summary>
/// <param name="ElementId">Element of the chain.</param>
/// <param name="Level">BFS level at which the element was discovered (0 = root dynamic).</param>
public readonly record struct ChainQueueEntry(ElementId ElementId, int Level);
