using Autodesk.Revit.DB;
using SmartCon.Core.Math;
using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Вставка и позиционирование фитингов через Revit API (Phase 5 + 8).
/// Реализация: SmartCon.Revit/Fittings/RevitFittingInsertService.cs
/// Вызывать только внутри Transaction (I-03).
/// </summary>
public interface IFittingInsertService
{
    /// <summary>
    /// Вставить FamilyInstance фитинга в указанную точку.
    /// Возвращает ElementId вставленного экземпляра или null если семейство/типоразмер не найден.
    /// </summary>
    ElementId? InsertFitting(Document doc, string familyName, string symbolName, XYZ position);

    /// <summary>
    /// Выровнять фитинг так, чтобы коннектор, соответствующий staticProxy по типу (CTC),
    /// совпал по позиции и ориентации со staticProxy.
    /// <paramref name="dynamicTypeCode"/> — тип коннектора dynamic-стороны (из FittingMappingRule).
    /// Используется для определения ориентации: коннектор фитинга с CTC == dynamicTypeCode → к трубе.
    /// Возвращает ConnectorProxy второго коннектора фитинга после выравнивания (null при ошибке).
    /// </summary>
    ConnectorProxy? AlignFittingToStatic(
        Document doc,
        ElementId fittingId,
        ConnectorProxy staticProxy,
        ITransformService transformSvc,
        IConnectorService connSvc,
        ConnectionTypeCode dynamicTypeCode = default);

    /// <summary>
    /// Удалить элемент из документа.
    /// </summary>
    void DeleteElement(Document doc, ElementId elementId);
}
