using Autodesk.Revit.DB;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Работа с таблицами поиска семейств (FamilySizeTableManager).
/// Реализация: SmartCon.Revit/Parameters/RevitLookupTableService.cs
/// Вызывать ВНЕ транзакции (EditFamily требует doc.IsModifiable == false).
/// </summary>
public interface ILookupTableService
{
    /// <summary>
    /// Существует ли в таблице поиска строка с данным радиусом коннектора?
    /// Возвращает false если у элемента нет size_lookup таблицы.
    /// </summary>
    bool ConnectorRadiusExistsInTable(Document doc, ElementId elementId,
        int connectorIndex, double radiusInternalUnits);

    /// <summary>
    /// Ближайший доступный радиус в таблице поиска.
    /// Возвращает targetRadiusInternalUnits если таблицы нет (fallback).
    /// </summary>
    double GetNearestAvailableRadius(Document doc, ElementId elementId,
        int connectorIndex, double targetRadiusInternalUnits);

    /// <summary>
    /// Есть ли у элемента size_lookup таблица, управляющая радиусом коннектора?
    /// </summary>
    bool HasLookupTable(Document doc, ElementId elementId, int connectorIndex);
}
