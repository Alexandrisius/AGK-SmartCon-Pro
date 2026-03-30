using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Extensions;
using RevitFamily = Autodesk.Revit.DB.Family;

namespace SmartCon.Revit.Family;

/// <summary>
/// Реализация IFamilyConnectorService.
///
/// Трубы / гибкие трубы (MEPCurve, FlexPipe):
///   — пишет код в BuiltInParameter.ALL_MODEL_DESCRIPTION типоразмера.
///   — вызывать ВНУТРИ транзакции проекта (I-03).
///
/// Фитинги (FamilyInstance):
///   — открывает семейство через EditFamily, находит ConnectorElement по Origin,
///     пишет в BuiltInParameter.ALL_MODEL_DESCRIPTION, грузит семейство обратно.
///   — EditFamily требует: doc.IsModifiable == false (нет открытой транзакции).
///   — LoadFamily выполняется через new Transaction(doc) — ITransactionService не подходит:
///     после EditFamily активный документ переключается на family doc.
///   — вызывать ВОВНЕ транзакции проекта.
/// </summary>
public sealed class RevitFamilyConnectorService : IFamilyConnectorService
{
    public bool SetConnectorTypeCode(Document doc, ElementId elementId,
                                     int connectorIndex, ConnectorTypeDefinition typeDef)
    {
        var element = doc.GetElement(elementId);
        if (element is null) return false;

        if (element is MEPCurve or FlexPipe)
            return SetPipeTypeDescription(doc, element, typeDef);

        var instance = element as FamilyInstance;
        if (instance is null) return false;

        // Revit API: EditFamily запрещён при открытой транзакции.
        if (doc.IsModifiable)
            throw new InvalidOperationException(
                "SetConnectorTypeCode для FamilyInstance должен вызываться вне транзакции. " +
                "Убедитесь, что вызов не обёрнут в RunInTransaction.");

        return SetFittingConnectorTypeCode(doc, instance, connectorIndex, typeDef);
    }

    // ── Фитинги (EditFamily) ───────────────────────────────────────────────────

    private bool SetFittingConnectorTypeCode(Document doc, FamilyInstance instance,
                                              int connectorIndex, ConnectorTypeDefinition typeDef)
    {
        RevitFamily? family = instance.Symbol?.Family;
        if (family is null) return false;

        // Origin нужного коннектора в глобальных координатах проекта.
        var cm = instance.GetConnectorManager();
        var conn = cm?.FindByIndex(connectorIndex);
        if (conn is null) return false;

        var targetOriginGlobal = conn.CoordinateSystem.Origin;
        var transform = instance.GetTransform();

        // EditFamily — doc.IsModifiable уже проверен выше.
        var familyDoc = doc.EditFamily(family);
        try
        {
            // Находим ConnectorElement с ближайшим origin.
            var connElems = new FilteredElementCollector(familyDoc)
                .OfCategory(BuiltInCategory.OST_ConnectorElem)
                .WhereElementIsNotElementType()
                .Cast<ConnectorElement>();

            ConnectorElement? target = null;
            double minDist = double.MaxValue;

            foreach (var ce in connElems)
            {
                var globalOrigin = transform.OfPoint(ce.Origin);
                var dist = globalOrigin.DistanceTo(targetOriginGlobal);

                if (dist < minDist)
                {
                    minDist = dist;
                    target = ce;
                }
            }

            // Допуск 0.1 фут (~30 мм) — исключает ложные совпадения.
            if (target is null || minDist > 0.1) return false;

            // Транзакция на family doc (отдельный документ — new Transaction допустим, I-03 не нарушается).
            using var familyTx = new Transaction(familyDoc, "SetConnectorDescription");
            familyTx.Start();

            // ALL_MODEL_DESCRIPTION — системный параметр «Описание».
            var descParam = target.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)
                         ?? target.LookupParameter("Description");

            if (descParam is null || descParam.IsReadOnly)
            {
                familyTx.RollBack();
                return false;
            }

            // Формат: "КОД.НАЗВАНИЕ.ОПИСАНИЕ" — читается через ConnectionTypeCode.Parse
            descParam.Set($"{typeDef.Code}.{typeDef.Name}.{typeDef.Description}");
            familyTx.Commit();

            // Явно освобождаем familyTx чтобы familyDoc.IsModifiable стал false
            familyTx.Dispose();

            // Загружаем изменённое семейство обратно в проект.
            // LoadFamily требует familyDoc.IsModifiable = false (транзакция закрыта).
            familyDoc.LoadFamily(doc, new FamilyLoadOptions());

            // Закрываем familyDoc (изменения уже загружены в проект)
            familyDoc.Close(false);

            return true;
        }
        finally
        {
            // familyDoc уже закрыт в try-блоке.
        }
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

    // ── Вспомогательный класс ─────────────────────────────────────────────────

    private sealed class FamilyLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = true;
            return true;
        }

        public bool OnSharedFamilyFound(RevitFamily sharedFamily, bool familyInUse,
            out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = true;
            return true;
        }
    }
}
