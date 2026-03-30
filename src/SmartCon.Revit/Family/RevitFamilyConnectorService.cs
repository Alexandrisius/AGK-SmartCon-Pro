using System;
using System.Diagnostics;
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
        if (family is null)
        {
            Debug.WriteLine("[SmartCon] SetFittingConnectorTypeCode: family is null");
            return false;
        }

        Debug.WriteLine($"[SmartCon] Processing family: {family.Name}");

        // Origin нужного коннектора в глобальных координатах проекта.
        var cm = instance.GetConnectorManager();
        var conn = cm?.FindByIndex(connectorIndex);
        if (conn is null)
        {
            Debug.WriteLine($"[SmartCon] Connector not found by index: {connectorIndex}");
            return false;
        }

        var targetOriginGlobal = conn.CoordinateSystem.Origin;
        Debug.WriteLine($"[SmartCon] Target connector origin (global): {targetOriginGlobal}");

        // Трансформ: локальное пространство семейства → глобальное пространство проекта.
        var transform = instance.GetTransform();

        // EditFamily — doc.IsModifiable уже проверен выше.
        var familyDoc = doc.EditFamily(family);
        Debug.WriteLine($"[SmartCon] EditFamily opened: {familyDoc.Title}");
        try
        {
            // OfCategory(OST_ConnectorElem) — правильный способ получить все ConnectorElement в семействе.
            var connElems = new FilteredElementCollector(familyDoc)
                .OfCategory(BuiltInCategory.OST_ConnectorElem)
                .WhereElementIsNotElementType()
                .Cast<ConnectorElement>()
                .ToList();

            Debug.WriteLine($"[SmartCon] Found {connElems.Count} ConnectorElement(s) in family");

            // Находим ConnectorElement с ближайшим к targetOriginGlobal origin:
            // переводим каждый локальный origin в глобальные координаты через transform.OfPoint().
            ConnectorElement? target = null;
            double minDist = double.MaxValue;

            foreach (var ce in connElems)
            {
                var localOrigin = ce.Origin;
                var globalOrigin = transform.OfPoint(localOrigin);
                var dist = globalOrigin.DistanceTo(targetOriginGlobal);

                Debug.WriteLine($"[SmartCon]   ConnectorElement Id={ce.Id.Value}, localOrigin={localOrigin}, globalOrigin={globalOrigin}, dist={dist}");

                if (dist < minDist)
                {
                    minDist = dist;
                    target = ce;
                }
            }

            Debug.WriteLine($"[SmartCon] Closest connector: Id={target?.Id.Value}, minDist={minDist}");

            // Допуск 0.1 фут (~30 мм) — исключает ложные совпадения.
            if (target is null || minDist > 0.1)
            {
                Debug.WriteLine($"[SmartCon] Target not found or distance too large (>0.1 ft)");
                return false;
            }

            // Логируем все параметры ConnectorElement для диагностики
            Debug.WriteLine($"[SmartCon] All parameters on target ConnectorElement (Id={target.Id.Value}):");
            foreach (Parameter p in target.Parameters)
            {
                var name = p.Definition?.Name ?? "N/A";
                var isShared = p.IsShared ? "Shared" : "Built-in";
                Debug.WriteLine($"[SmartCon]   Param: {name}, Type={isShared}, ReadOnly={p.IsReadOnly}, Value={p.AsValueString()}");
            }

            // Транзакция на family doc (отдельный документ — new Transaction допустим, I-03 не нарушается).
            using var familyTx = new Transaction(familyDoc, "SetConnectorDescription");
            familyTx.Start();

            // Пробуем ALL_MODEL_DESCRIPTION (системный «Описание»),
            // затем LookupParameter("Description") как запасной вариант.
            var descParam = target.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)
                         ?? target.LookupParameter("Description");

            Debug.WriteLine($"[SmartCon] descParam from ALL_MODEL_DESCRIPTION or LookupParameter('Description'): {descParam?.Definition?.Name ?? "NULL"}");

            // Дополнительно пробуем LookupParameter с русским именем
            if (descParam is null)
            {
                descParam = target.LookupParameter("Описание");
                Debug.WriteLine($"[SmartCon] Trying LookupParameter('Описание'): {descParam?.Definition?.Name ?? "NULL"}");
            }

            // Пробуем любой параметр с "Description" в имени
            if (descParam is null)
            {
                foreach (Parameter p in target.Parameters)
                {
                    var name = p.Definition?.Name ?? "";
                    if (name.Contains("escription", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Описание")) // "Описание"
                    {
                        Debug.WriteLine($"[SmartCon] Found candidate parameter: {name}");
                        descParam = p;
                        break;
                    }
                }
            }

            if (descParam is null)
            {
                Debug.WriteLine("[SmartCon] ERROR: descParam is null - no Description parameter found!");
                familyTx.RollBack();
                return false;
            }

            if (descParam.IsReadOnly)
            {
                Debug.WriteLine($"[SmartCon] ERROR: descParam '{descParam.Definition?.Name}' is ReadOnly!");
                familyTx.RollBack();
                return false;
            }

            // Формат: "КОД.НАЗВАНИЕ.ОПИСАНИЕ" — читается через ConnectionTypeCode.Parse
            var value = $"{typeDef.Code}.{typeDef.Name}.{typeDef.Description}";
            Debug.WriteLine($"[SmartCon] Setting parameter '{descParam.Definition?.Name}' to value: {value}");
            descParam.Set(value);
            familyTx.Commit();
            Debug.WriteLine("[SmartCon] familyTx committed successfully");

            // Явно освобождаем familyTx чтобы familyDoc.IsModifiable стал false
            familyTx.Dispose();
            Debug.WriteLine($"[SmartCon] familyTx disposed, familyDoc.IsModifiable = {familyDoc.IsModifiable}");

            // Загружаем изменённое семейство обратно в проект.
            // LoadFamily требует familyDoc.IsModifiable = false (транзакция закрыта).
            Debug.WriteLine("[SmartCon] Starting LoadFamily...");
            var loadedFamily = familyDoc.LoadFamily(doc, new FamilyLoadOptions());
            Debug.WriteLine($"[SmartCon] LoadFamily completed, loaded: {loadedFamily?.Name}");

            // Закрываем familyDoc (изменения уже загружены в проект)
            familyDoc.Close(false);
            Debug.WriteLine("[SmartCon] familyDoc closed");

            return true;
        }
        finally
        {
            // familyDoc уже закрыт в try-блоке после SaveAs.
            // Не вызываем Close повторно.
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
