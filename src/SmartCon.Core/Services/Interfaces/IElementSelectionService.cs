using Autodesk.Revit.DB;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Интерактивный выбор MEP-элементов в Revit.
/// Фильтрация — деталь реализации (Revit-слой). Core не ссылается на RevitAPIUI (I-09).
/// Реализация: SmartCon.Revit/Selection/ElementSelectionService.cs
/// </summary>
public interface IElementSelectionService
{
    /// <summary>
    /// Выбор одного элемента со свободным коннектором.
    /// Реализация применяет ISelectionFilter на стороне Revit.
    /// Возвращает (ElementId, XYZ clickPoint) или null при ESC/отмене.
    /// </summary>
    (ElementId ElementId, XYZ ClickPoint)? PickElementWithFreeConnector(string statusMessage);
}
