using Autodesk.Revit.DB;
using SmartCon.Core.Models;

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
    /// constraints — ограничения по другим DN-столбцам (для multi-query фитингов).
    /// </summary>
    bool ConnectorRadiusExistsInTable(Document doc, ElementId elementId,
        int connectorIndex, double radiusInternalUnits,
        IReadOnlyList<LookupColumnConstraint>? constraints = null);

    /// <summary>
    /// Ближайший доступный радиус в таблице поиска.
    /// Возвращает targetRadiusInternalUnits если таблицы нет (fallback).
    /// constraints — ограничения по другим DN-столбцам (для multi-query фитингов).
    /// </summary>
    double GetNearestAvailableRadius(Document doc, ElementId elementId,
        int connectorIndex, double targetRadiusInternalUnits,
        IReadOnlyList<LookupColumnConstraint>? constraints = null);

    /// <summary>
    /// Есть ли у элемента size_lookup таблица, управляющая радиусом коннектора?
    /// </summary>
    bool HasLookupTable(Document doc, ElementId elementId, int connectorIndex);

    /// <summary>
    /// Получить ВСЕ строки таблицы поиска как полные конфигурации.
    /// Каждая строка содержит радиусы ВСЕХ коннекторов (по маппингу столбец→коннектор).
    /// Если constraints заданы — фильтрует строки как GetAvailableSizes.
    /// Если constraints = null — возвращает все строки без фильтрации.
    /// Вызывать ВНЕ транзакции.
    /// </summary>
    IReadOnlyList<SizeTableRow> GetAllSizeRows(Document doc, ElementId elementId,
        int targetConnectorIndex,
        IReadOnlyList<LookupColumnConstraint>? constraints = null);
}
