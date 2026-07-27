using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Family;

/// <summary>
/// Реализация IFamilyConnectorService.
///
/// Трубы / гибкие трубы (MEPCurve, FlexPipe):
///   — пишет код в BuiltInParameter.ALL_MODEL_DESCRIPTION типоразмера.
///   — вызывать ВНУТРИ транзакции проекта (I-03).
///
/// Фитинги (FamilyInstance):
///   — НЕ поддерживаются здесь: CTC для фитингов идёт через VirtualCtcStore +
///     CtcFamilyWriter.ApplyFittingCtcToFamily (index/primary matching, #161).
///     Ранее здесь был EditFamily-путь с геометрическим поиском ConnectorElement
///     по origin — удалён: ломался при рассинхроне шаблона и экземпляра.
/// </summary>
public sealed class RevitFamilyConnectorService : IFamilyConnectorService
{
    public bool SetConnectorTypeCode(Document doc, ElementId elementId,
                                     int connectorIndex, ConnectorTypeDefinition typeDef)
    {
        using var _scope = SmartConLogger.BeginScope("FamilyConn",
            ("Method", "SetConnectorTypeCode"),
            ("ElementId", elementId.GetValue()),
            ("ConnectorIndex", connectorIndex));

        var element = doc.GetElement(elementId);
        if (element is null) return false;

        if (element is MEPCurve or FlexPipe)
            return SetPipeTypeDescription(doc, element, typeDef);

        SmartConLogger.Info($"SetConnectorTypeCode: element is not MEPCurve/FlexPipe ({element.GetType().Name}) — CTC фитингов идёт через VirtualCtcStore/CtcFamilyWriter, пропущено");
        return false;
    }

    // ── Трубы и гибкие трубы ──────────────────────────────────────────────────

    /// <summary>
    /// Записывает "КОД.НАЗВАНИЕ.ОПИСАНИЕ" в ALL_MODEL_DESCRIPTION типоразмера трубы.
    /// Оба коннектора трубы одинаковы — достаточно одной записи на уровне типа.
    /// Вызывать внутри транзакции проекта (I-03).
    /// </summary>
    private static bool SetPipeTypeDescription(Document doc, Element element, ConnectorTypeDefinition typeDef)
    {
        var typeId = element.GetTypeId();
        if (typeId == ElementId.InvalidElementId) return false;

        var elemType = doc.GetElement(typeId);
        if (elemType is null) return false;

        var param = elemType.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION);
        if (param is null || param.IsReadOnly) return false;

        // Формат: "КОД.НАЗВАНИЕ.ОПИСАНИЕ" — читается через ConnectionTypeCode.Parse
        param.Set($"{typeDef.Code}.{typeDef.Name}.{typeDef.Description}");
        return true;
    }
}
