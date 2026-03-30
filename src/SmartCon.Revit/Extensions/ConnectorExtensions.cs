using Autodesk.Revit.DB;
using SmartCon.Core.Models;
using SmartCon.Revit.Wrappers;

namespace SmartCon.Revit.Extensions;

/// <summary>
/// Extension-методы для работы с Revit Connector.
/// </summary>
public static class ConnectorExtensions
{
    /// <summary>
    /// Получить все коннекторы элемента как список Connector.
    /// Исключает ConnectorType.Curve (I-08).
    /// </summary>
    public static IReadOnlyList<Connector> GetPipeConnectors(this ConnectorManager connectorManager)
    {
        ArgumentNullException.ThrowIfNull(connectorManager);

        var result = new List<Connector>();

        foreach (Connector connector in connectorManager.Connectors)
        {
            if (connector.ConnectorType == ConnectorType.End ||
                connector.ConnectorType == ConnectorType.MasterSurface)
            {
                result.Add(connector);
            }
        }

        return result;
    }

    /// <summary>
    /// Получить все свободные (не подключённые) коннекторы элемента.
    /// Исключает ConnectorType.Curve (I-08).
    /// </summary>
    public static IReadOnlyList<Connector> GetFreeConnectors(this ConnectorManager connectorManager)
    {
        ArgumentNullException.ThrowIfNull(connectorManager);

        var result = new List<Connector>();

        foreach (Connector connector in connectorManager.Connectors)
        {
            if (connector.ConnectorType == ConnectorType.Curve)
                continue;

            if (!connector.IsConnected)
            {
                result.Add(connector);
            }
        }

        return result;
    }

    /// <summary>
    /// Преобразовать Connector в ConnectorProxy.
    /// </summary>
    public static ConnectorProxy ToProxy(this Connector connector)
    {
        return ConnectorWrapper.ToProxy(connector);
    }

    /// <summary>
    /// Найти коннектор по индексу (Id).
    /// </summary>
    public static Connector? FindByIndex(this ConnectorManager connectorManager, int index)
    {
        ArgumentNullException.ThrowIfNull(connectorManager);

        foreach (Connector connector in connectorManager.Connectors)
        {
            if ((int)connector.Id == index)
            {
                return connector;
            }
        }

        return null;
    }

    /// <summary>
    /// Найти ближайший свободный коннектор к заданной точке.
    /// Исключает ConnectorType.Curve (I-08).
    /// Возвращает null если нет свободных коннекторов.
    /// </summary>
    public static Connector? FindNearestFreeConnector(
        this ConnectorManager connectorManager, XYZ point)
    {
        ArgumentNullException.ThrowIfNull(connectorManager);
        ArgumentNullException.ThrowIfNull(point);

        Connector? nearest = null;
        var minDistance = double.MaxValue;

        foreach (Connector connector in connectorManager.Connectors)
        {
            if (connector.ConnectorType == ConnectorType.Curve)
                continue;

            if (connector.IsConnected)
                continue;

            var distance = connector.Origin.DistanceTo(point);

            if (distance < minDistance)
            {
                minDistance = distance;
                nearest = connector;
            }
        }

        return nearest;
    }
}
