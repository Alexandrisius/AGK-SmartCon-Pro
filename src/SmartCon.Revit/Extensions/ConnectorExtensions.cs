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
#if NETFRAMEWORK
        if (connectorManager is null) throw new ArgumentNullException(nameof(connectorManager));
#else
        ArgumentNullException.ThrowIfNull(connectorManager);
#endif

        var result = new List<Connector>();

        foreach (Connector connector in connectorManager.Connectors)
        {
            if (connector.ConnectorType == ConnectorType.End ||
#if REVIT2027_OR_GREATER
                // Revit 2027: MasterSurface удалён из enum (deprecated в 2026,
                // замена MainSurface; в 2021 API MainSurface ещё нет — там MasterSurface)
                connector.ConnectorType == ConnectorType.MainSurface)
#else
                connector.ConnectorType == ConnectorType.MasterSurface)
#endif
            {
                result.Add(connector);
            }
        }

        return result;
    }

    /// <summary>
    /// Получить все свободные (не подключённые) трубопроводные коннекторы элемента.
    /// Исключает ConnectorType.Curve (I-08). Фильтрует по Domain.DomainPiping.
    /// </summary>
    public static IReadOnlyList<Connector> GetFreeConnectors(this ConnectorManager connectorManager)
    {
#if NETFRAMEWORK
        if (connectorManager is null) throw new ArgumentNullException(nameof(connectorManager));
#else
        ArgumentNullException.ThrowIfNull(connectorManager);
#endif

        var result = new List<Connector>();

        foreach (Connector connector in connectorManager.Connectors)
        {
            if (connector.ConnectorType == ConnectorType.Curve)
                continue;

            if (connector.Domain != Domain.DomainPiping)
                continue;

            if (!connector.IsConnected)
            {
                result.Add(connector);
            }
        }

        return result;
    }

    /// <summary>
    /// Безопасная проверка формы коннектора.
    /// Connector.Shape getter бросает InvalidOperationException если у коннектора нет формы —
    /// такой коннектор считается не круглым.
    /// </summary>
    public static bool IsRoundSafe(this Connector connector)
    {
#if NETFRAMEWORK
        if (connector is null) throw new ArgumentNullException(nameof(connector));
#else
        ArgumentNullException.ThrowIfNull(connector);
#endif

        try
        {
            return connector.Shape == ConnectorProfileType.Round;
        }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Безопасное чтение радиуса: для не-круглых коннекторов (электрические, Invalid shape)
    /// Connector.Radius бросает InvalidOperationException — возвращаем 0.
    /// См. issue #137.
    /// </summary>
    public static double GetRadiusSafe(this Connector connector)
    {
#if NETFRAMEWORK
        if (connector is null) throw new ArgumentNullException(nameof(connector));
#else
        ArgumentNullException.ThrowIfNull(connector);
#endif

        return connector.IsRoundSafe() ? connector.Radius : 0.0;
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
#if NETFRAMEWORK
        if (connectorManager is null) throw new ArgumentNullException(nameof(connectorManager));
#else
        ArgumentNullException.ThrowIfNull(connectorManager);
#endif

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
    /// Найти ближайший свободный трубопроводный коннектор к заданной точке.
    /// Исключает ConnectorType.Curve (I-08). Фильтрует по Domain.DomainPiping.
    /// Возвращает null если нет свободных коннекторов.
    /// </summary>
    public static Connector? FindNearestFreeConnector(
        this ConnectorManager connectorManager, XYZ point)
    {
#if NETFRAMEWORK
        if (connectorManager is null) throw new ArgumentNullException(nameof(connectorManager));
        if (point is null) throw new ArgumentNullException(nameof(point));
#else
        ArgumentNullException.ThrowIfNull(connectorManager);
        ArgumentNullException.ThrowIfNull(point);
#endif

        Connector? nearest = null;
        var minDistance = double.MaxValue;

        foreach (Connector connector in connectorManager.Connectors)
        {
            if (connector.ConnectorType == ConnectorType.Curve)
                continue;

            if (connector.Domain != Domain.DomainPiping)
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
