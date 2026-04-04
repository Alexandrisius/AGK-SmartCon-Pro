using Autodesk.Revit.DB;
using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Получение списка доступных размеров DN для динамического семейства.
/// Реализация: SmartCon.Revit/Parameters/RevitDynamicSizeResolver.cs
/// Вызывать ВНЕ транзакции (EditFamily требует doc.IsModifiable == false).
/// </summary>
public interface IDynamicSizeResolver
{
    /// <summary>
    /// Возвращает все доступные радиусы для указанного коннектора элемента.
    /// Сначала пробует LookupTable, если нет — перебирает все FamilySymbol.
    /// </summary>
    IReadOnlyList<SizeOption> GetAvailableSizes(Document doc, ElementId elementId, int connectorIndex);
}
