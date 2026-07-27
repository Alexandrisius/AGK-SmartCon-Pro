using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Extensions;
using SmartCon.Revit.Wrappers;

namespace SmartCon.Revit.Selection;

/// <summary>
/// Реализация IConnectorService через Revit API.
/// Все вызовы должны выполняться на Revit main thread.
/// </summary>
public sealed class ConnectorService : IConnectorService
{
    public ConnectorProxy? GetNearestFreeConnector(Document doc, ElementId elementId, XYZ clickPoint)
    {
        var element = doc.GetElement(elementId);
        if (element is null) return null;

        var cm = element.GetConnectorManager();
        if (cm is null) return null;

        var nearest = cm.FindNearestFreeConnector(clickPoint);
        return nearest?.ToProxy();
    }

    public ConnectorProxy? RefreshConnector(Document doc, ElementId elementId, int connectorIndex)
    {
        var element = doc.GetElement(elementId);
        if (element is null) return null;

        var cm = element.GetConnectorManager();
        if (cm is null) return null;

        var connector = cm.FindByIndex(connectorIndex);
        return connector?.ToProxy();
    }

    public bool ConnectTo(Document doc,
        ElementId elementId1, int connectorIndex1,
        ElementId elementId2, int connectorIndex2)
    {
        var element1 = doc.GetElement(elementId1);
        var element2 = doc.GetElement(elementId2);
        if (element1 is null || element2 is null) return false;

        var cm1 = element1.GetConnectorManager();
        var cm2 = element2.GetConnectorManager();
        if (cm1 is null || cm2 is null) return false;

        var conn1 = cm1.FindByIndex(connectorIndex1);
        var conn2 = cm2.FindByIndex(connectorIndex2);
        if (conn1 is null || conn2 is null) return false;

        conn1.ConnectTo(conn2);
        return true;
    }

    public IReadOnlyList<ConnectorProxy> GetAllFreeConnectors(Document doc, ElementId elementId)
    {
        var element = doc.GetElement(elementId);
        if (element is null) return [];

        var cm = element.GetConnectorManager();
        if (cm is null) return [];

        var connectors = cm.Connectors
                 .Cast<Connector>()
                 .Where(c => c.ConnectorType != ConnectorType.Curve && !c.IsConnected)
                 .Where(c => c.Domain == Domain.DomainPiping)
                 .ToList();

        var ordered = SortDeterministically(element, connectors);

        SmartConLogger.Debug($"GetAllFreeConnectors: element={elementId.GetValue()}, " +
            $"order=[{string.Join(",", ordered.Select(c => (int)c.Id))}]");

        return ordered.Select(c => c.ToProxy()).ToList();
    }

    public void DisconnectAllFromConnector(Document doc, ElementId elementId, int connectorIndex)
    {
        var element = doc.GetElement(elementId);
        if (element is null) return;

        var cm = element.GetConnectorManager();
        if (cm is null) return;

        var connector = cm.FindByIndex(connectorIndex);
        if (connector is null) return;

        // DisconnectFrom возвращает набор отсоединённых коннекторов;
        // вызываем пока остаются соединения (могут быть каскадные).
        while (connector.IsConnected)
        {
            connector.DisconnectFrom(connector.AllRefs.Cast<Connector>().First());
        }
    }

    public IReadOnlyList<ConnectorProxy> GetAllConnectors(Document doc, ElementId elementId)
    {
        var element = doc.GetElement(elementId);
        if (element is null) return [];

        var cm = element.GetConnectorManager();
        if (cm is null) return [];

        var connectors = cm.Connectors
                 .Cast<Connector>()
                 .Where(c => c.ConnectorType != ConnectorType.Curve)
                 .Where(c => c.Domain == Domain.DomainPiping)
                 .ToList();

        var ordered = SortDeterministically(element, connectors);

        SmartConLogger.Debug($"GetAllConnectors: element={elementId.GetValue()}, " +
            $"order=[{string.Join(",", ordered.Select(c => (int)c.Id))}]");

        return ordered.Select(c => c.ToProxy()).ToList();
    }

    /// <summary>
    /// ConnectorManager.Connectors (ConnectorSet) enumerates connectors in a random
    /// order that changes from call to call (Autodesk Community, issue #163) — every
    /// consumer gets a deterministic geometric order instead: X asc → Z desc → Y asc,
    /// tie-break by Connector.Id (see SmartCon.Core/Math/ConnectorOrdering.cs).
    /// </summary>
    private static IReadOnlyList<Connector> SortDeterministically(Element element, List<Connector> connectors)
        => ConnectorOrdering.OrderByPosition(
            connectors,
            c => GetDeterministicSortKey(element, c),
            c => (int)c.Id);

    /// <summary>
    /// Sort key invariant to the element's rigid transforms (move/rotate), so the order
    /// stays stable while PipeConnectEditor re-aligns the element between refreshes:
    /// FamilyInstance → family-local coordinates (GetTotalTransform inverse);
    /// MEPCurve (Pipe/Duct/FlexPipe) → projection onto the location axis;
    /// fallback → world coordinates.
    /// </summary>
    private static Vec3 GetDeterministicSortKey(Element element, Connector connector)
    {
        if (element is FamilyInstance familyInstance)
        {
            var transform = familyInstance.GetTotalTransform();
            if (transform is not null)
            {
                var local = transform.Inverse.OfPoint(connector.Origin);
                return new Vec3(local.X, local.Y, local.Z);
            }
        }

        if (element is MEPCurve { Location: LocationCurve locationCurve })
        {
            var curve = locationCurve.Curve;
            var start = curve.GetEndPoint(0);
            var axis = curve.GetEndPoint(1) - start;
            if (axis.GetLength() > VectorUtils.Tolerance)
            {
                var projection = (connector.Origin - start).DotProduct(axis.Normalize());
                return new Vec3(projection, 0, 0);
            }
        }

        return new Vec3(connector.Origin.X, connector.Origin.Y, connector.Origin.Z);
    }
}
