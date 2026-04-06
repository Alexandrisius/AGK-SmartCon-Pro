using Autodesk.Revit.DB;
using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Вставка reducer-а между двумя коннекторами с разным DN.
/// Используется при IncrementChainDepth когда AdjustSize не смог подогнать элемент.
/// </summary>
public interface INetworkMover
{
    /// <summary>
    /// Вставить reducer между parentConn и childConn.
    /// Reducer выравнивается к parentConn. Возвращает ElementId вставленного reducer или null.
    /// </summary>
    ElementId? InsertReducer(Document doc,
        ConnectorProxy parentConn, ConnectorProxy childConn);
}
