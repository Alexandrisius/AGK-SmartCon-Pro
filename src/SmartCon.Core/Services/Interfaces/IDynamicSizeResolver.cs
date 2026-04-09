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
    /// constraints — ограничения по другим DN-столбцам (для multi-query фитингов).
    /// Если constraints не null, dropdown показывает только строки таблицы,
    /// где значения других столбцов соответствуют ограничениям.
    /// </summary>
    IReadOnlyList<SizeOption> GetAvailableSizes(Document doc, ElementId elementId,
        int connectorIndex,
        IReadOnlyList<LookupColumnConstraint>? constraints = null);

    /// <summary>
    /// Возвращает все доступные типоразмеры FamilyInstance с радиусами ВСЕХ коннекторов.
    /// Каждый элемент списка — полная конфигурация семейства (напр. DN 65×50×65).
    /// DisplayName формируется как "DN {target} × DN {other1} [× ...]".
    /// Вызывать ВНЕ транзакции (EditFamily).
    /// </summary>
    IReadOnlyList<FamilySizeOption> GetAvailableFamilySizes(Document doc, ElementId elementId,
        int targetConnectorIndex);
}
