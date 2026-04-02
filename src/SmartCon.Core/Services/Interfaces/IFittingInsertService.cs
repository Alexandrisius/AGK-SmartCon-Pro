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
    /// Выровнять фитинг так, чтобы его ближайший к staticOrigin коннектор
    /// совпал по позиции и ориентации со staticProxy.
    /// Возвращает ConnectorProxy второго коннектора фитинга после выравнивания (null при ошибке).
    /// </summary>
    ConnectorProxy? AlignFittingToStatic(
        Document doc,
        ElementId fittingId,
        ConnectorProxy staticProxy,
        ITransformService transformSvc,
        IConnectorService connSvc);

    /// <summary>
    /// Удалить элемент из документа.
    /// </summary>
    void DeleteElement(Document doc, ElementId elementId);
}
