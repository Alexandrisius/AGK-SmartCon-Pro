using Autodesk.Revit.DB;
using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Запись типа соединения в Description коннектора/типоразмера через Revit API.
/// Реализация: SmartCon.Revit/Family/RevitFamilyConnectorService.cs
/// </summary>
public interface IFamilyConnectorService
{
    /// <summary>
    /// Записать тип соединения в Description коннектора или типоразмера трубы.
    /// Формат записи: "КОД.НАЗВАНИЕ.ОПИСАНИЕ" (читается через ConnectionTypeCode.Parse).
    /// Для труб — вызывать ВНУТРИ транзакции (I-03).
    /// Для фитингов — вызывать ВОВНЕ транзакции (EditFamily требует IsModifiable==false).
    /// </summary>
    bool SetConnectorTypeCode(Document doc, ElementId elementId,
                              int connectorIndex, ConnectorTypeDefinition typeDef);
}
