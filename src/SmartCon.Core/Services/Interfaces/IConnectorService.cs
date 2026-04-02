using Autodesk.Revit.DB;
using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Работа с коннекторами элементов: получение proxy, поиск ближайшего, ConnectTo.
/// Реализация: SmartCon.Revit/Selection/ConnectorService.cs
/// </summary>
public interface IConnectorService
{
    /// <summary>
    /// Получить ConnectorProxy ближайшего свободного коннектора к точке клика.
    /// Возвращает null если нет свободных коннекторов.
    /// </summary>
    ConnectorProxy? GetNearestFreeConnector(Document doc, ElementId elementId, XYZ clickPoint);

    /// <summary>
    /// Перечитать актуальное состояние коннектора (после трансформации).
    /// </summary>
    ConnectorProxy? RefreshConnector(Document doc, ElementId elementId, int connectorIndex);

    /// <summary>
    /// Соединить два коннектора через Connector.ConnectTo().
    /// Возвращает true при успехе.
    /// </summary>
    bool ConnectTo(Document doc,
        ElementId elementId1, int connectorIndex1,
        ElementId elementId2, int connectorIndex2);

    /// <summary>
    /// Получить все свободные коннекторы элемента (исключая ConnectorType.Curve).
    /// Используется для ComboBox выбора коннектора в PipeConnectEditor (Phase 8).
    /// </summary>
    IReadOnlyList<ConnectorProxy> GetAllFreeConnectors(Document doc, ElementId elementId);
}
