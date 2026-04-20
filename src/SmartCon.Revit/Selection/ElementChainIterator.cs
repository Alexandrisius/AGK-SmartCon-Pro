using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Logging;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Extensions;
using SmartCon.Core.Compatibility;

namespace SmartCon.Revit.Selection;

/// <summary>
/// BFS-обход соединённых MEP-элементов по уровням.
/// Строит ConnectionGraph с Levels и SavedConnections.
/// Реализация IElementChainIterator (интерфейс в Core).
/// </summary>
public sealed class ElementChainIterator : IElementChainIterator
{
    /// <inheritdoc />
    public ConnectionGraph BuildGraph(Document doc, ElementId startElementId,
        HashSet<ElementId>? stopAtElements = null)
    {
        var comparer = ElementIdEqualityComparer.Instance;
        var builder = new ConnectionGraphBuilder(startElementId);
        var visited = new HashSet<ElementId>(comparer) { startElementId };

        // 1. Определить "заблокированный" коннектор dynamic (ведёт к static)
        int excludedConnIdx = -1;
        var startElem = doc.GetElement(startElementId);
        var startCm = GetConnectorManager(startElem);
        if (startCm is not null && stopAtElements is not null)
        {
            foreach (Connector conn in startCm.Connectors)
            {
                if (conn.ConnectorType == ConnectorType.Curve) continue;
                if (!conn.IsConnected) continue;

                bool found = false;
                foreach (Connector refConn in conn.AllRefs)
                {
                    if (refConn.Owner is not null && stopAtElements.Contains(refConn.Owner.Id))
                    {
                        excludedConnIdx = conn.Id;
                        found = true;
                        break;
                    }
                }
                if (found) break;
            }
        }

        SmartConLogger.Info($"[Chain] BuildGraph: start={startElementId.GetValue()}, excludedConnIdx={excludedConnIdx}");

        // 2. BFS по уровням
        List<ElementId> currentLevelIds = [startElementId];
        int bfsLevel = 0;

        while (currentLevelIds.Count > 0)
        {
            var nextLevelIds = new List<ElementId>();
            bfsLevel++;

            foreach (var elemId in currentLevelIds)
            {
                var elem = doc.GetElement(elemId);
                var cm = GetConnectorManager(elem);
                if (cm is null) continue;

                foreach (Connector conn in cm.Connectors)
                {
                    if (conn.ConnectorType == ConnectorType.Curve) continue;
                    if (comparer.Equals(elemId, startElementId) && conn.Id == excludedConnIdx) continue;
                    if (!conn.IsConnected) continue;

                    foreach (Connector refConn in conn.AllRefs)
                    {
                        if (refConn.Owner is null) continue;
                        var neighborId = refConn.Owner.Id;
                        if (stopAtElements is not null && stopAtElements.Contains(neighborId)) continue;
                        if (visited.Contains(neighborId)) continue;

                        visited.Add(neighborId);
                        nextLevelIds.Add(neighborId);
                        builder.AddNode(neighborId);
                        builder.AddElementAtLevel(bfsLevel, neighborId);
                        builder.AddEdge(new ConnectionEdge(
                            elemId, conn.Id, neighborId, refConn.Id));

                        builder.SaveConnection(elemId, new ConnectionRecord(
                            elemId, conn.Id, neighborId, refConn.Id));
                        builder.SaveConnection(neighborId, new ConnectionRecord(
                            neighborId, refConn.Id, elemId, conn.Id));
                    }
                }
            }

            currentLevelIds = nextLevelIds;
        }

        var graph = builder.Build();

        SmartConLogger.Info($"[Chain] BuildGraph done: {graph.TotalChainElements} elements, {graph.MaxLevel} levels");
        return graph;
    }

    /// <inheritdoc />
    public IReadOnlyList<ConnectorProxy> GetChainEndConnectors(
        Document doc, ConnectionGraph graph)
    {
        var result = new List<ConnectorProxy>();
        foreach (var elemId in graph.Nodes)
        {
            var elem = doc.GetElement(elemId);
            var cm = GetConnectorManager(elem);
            if (cm is null) continue;

            foreach (Connector conn in cm.Connectors)
            {
                if (conn.ConnectorType == ConnectorType.Curve) continue;
                if (conn.IsConnected) continue;
                result.Add(conn.ToProxy());
            }
        }
        return result;
    }

    private static ConnectorManager? GetConnectorManager(Element? elem)
        => elem switch
        {
            FamilyInstance fi => fi.MEPModel?.ConnectorManager,
            MEPCurve mc => mc.ConnectorManager,
            _ => null
        };
}
