using Autodesk.Revit.DB;
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

        return cm.Connectors
                 .Cast<Connector>()
                 .Where(c => c.ConnectorType != ConnectorType.Curve && !c.IsConnected)
                 .Select(c => c.ToProxy())
                 .ToList();
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

        return cm.Connectors
                 .Cast<Connector>()
                 .Where(c => c.ConnectorType != ConnectorType.Curve)
                 .Select(c => c.ToProxy())
                 .ToList();
    }
}
