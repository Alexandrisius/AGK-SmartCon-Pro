using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Logging;
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
            SmartConLogger.Info("[SmartCon] SetFittingConnectorTypeCode: family is null");
            return false;
        }

        SmartConLogger.Info($"[SmartCon] Processing family: {family.Name}");

        // Origin нужного коннектора в глобальных координатах проекта.
        var cm = instance.GetConnectorManager();
        var conn = cm?.FindByIndex(connectorIndex);
        if (conn is null)
        {
            SmartConLogger.Info($"[SmartCon] Connector not found by index: {connectorIndex}");
            return false;
        }

        var targetOriginGlobal = conn.CoordinateSystem.Origin;
        var transform = instance.GetTransform();

        SmartConLogger.Info($"[SmartCon] conn.CoordinateSystem.Origin (world) = ({targetOriginGlobal.X:F6}, {targetOriginGlobal.Y:F6}, {targetOriginGlobal.Z:F6})");
        SmartConLogger.Info($"[SmartCon] GetTransform: Origin=({transform.Origin.X:F6},{transform.Origin.Y:F6},{transform.Origin.Z:F6})");
        SmartConLogger.Info($"[SmartCon] GetTransform: BasisX=({transform.BasisX.X:F4},{transform.BasisX.Y:F4},{transform.BasisX.Z:F4})");
        SmartConLogger.Info($"[SmartCon] GetTransform: BasisY=({transform.BasisY.X:F4},{transform.BasisY.Y:F4},{transform.BasisY.Z:F4})");
        SmartConLogger.Info($"[SmartCon] GetTransform: BasisZ=({transform.BasisZ.X:F4},{transform.BasisZ.Y:F4},{transform.BasisZ.Z:F4})");
        SmartConLogger.Info($"[SmartCon] GetTransform: Scale={transform.Scale:F6}, IsIdentity={transform.IsIdentity}");

        // Также логируем цель в локальных координатах семейства для кросс-проверки.
        var targetOriginLocal = transform.Inverse.OfPoint(targetOriginGlobal);
        SmartConLogger.Info($"[SmartCon] targetOriginLocal (family space) = ({targetOriginLocal.X:F6}, {targetOriginLocal.Y:F6}, {targetOriginLocal.Z:F6})");

        // GetTransform() НЕ учитывает HandFlipped/FacingFlipped.
        // Connector world positions учитывают flip → inverse даёт зеркальные локальные координаты.
        // Для корректного сравнения с CE.Origin (unflipped family space) — компенсируем flip.
        SmartConLogger.Info($"[SmartCon] HandFlipped={instance.HandFlipped}, FacingFlipped={instance.FacingFlipped}");
        if (instance.HandFlipped)
            targetOriginLocal = new XYZ(-targetOriginLocal.X, targetOriginLocal.Y, targetOriginLocal.Z);
        if (instance.FacingFlipped)
            targetOriginLocal = new XYZ(targetOriginLocal.X, -targetOriginLocal.Y, targetOriginLocal.Z);
        if (instance.HandFlipped || instance.FacingFlipped)
            SmartConLogger.Info($"[SmartCon] targetOriginLocal (flip-corrected) = ({targetOriginLocal.X:F6}, {targetOriginLocal.Y:F6}, {targetOriginLocal.Z:F6})");

        // EditFamily — doc.IsModifiable уже проверен выше.
        var familyDoc = doc.EditFamily(family);
        SmartConLogger.Info($"[SmartCon] EditFamily opened: {familyDoc.Title}");
        try
        {
            // OfCategory(OST_ConnectorElem) — правильный способ получить все ConnectorElement в семействе.
            var connElems = new FilteredElementCollector(familyDoc)
                .OfCategory(BuiltInCategory.OST_ConnectorElem)
                .WhereElementIsNotElementType()
                .Cast<ConnectorElement>()
                .ToList();

            SmartConLogger.Info($"[SmartCon] Found {connElems.Count} ConnectorElement(s) in family");

            // Поиск ConnectorElement по направлению в локальных координатах семейства.
            // Для параметрических фитингов (отводы, краны) размер изменяется,
            // поэтому ce.Origin масштабируется, но НАПРАВЛЕНИЕ сохраняется.
            // Алгоритм: dot product нормированных векторов targetOriginLocal и ce.Origin.
            // Score=2.0  → точное совпадение позиции (distLocal<0.001 ft)
            // Score≈1.0  → одинаковое направление (параметрическая семья другого размера)
            // Score<0.99 → нет совпадения
            var targetLen = targetOriginLocal.GetLength();
            var targetDir = targetLen > 1e-6 ? targetOriginLocal.Divide(targetLen) : null;

            ConnectorElement? target = null;
            double bestScore = -2.0;
            foreach (var ce in connElems)
            {
                var distLocal = ce.Origin.DistanceTo(targetOriginLocal);
                double score;
                if (distLocal < 0.001) // точное совпадение (~0.3 мм)
                {
                    score = 2.0;
                }
                else if (targetDir is not null && ce.Origin.GetLength() > 1e-6)
                {
                    score = ce.Origin.Normalize().DotProduct(targetDir);
                }
                else
                {
                    score = -distLocal; // fallback: ближайшая по расстоянию
                }
                SmartConLogger.Info($"[SmartCon]   CE Id={ce.Id.Value}: origin=({ce.Origin.X:F4},{ce.Origin.Y:F4},{ce.Origin.Z:F4}) distLocal={distLocal:F4} score={score:F4}");
                if (score > bestScore) { bestScore = score; target = ce; }
            }

            SmartConLogger.Info($"[SmartCon] Best match: Id={target?.Id.Value}, bestScore={bestScore:F4}");

            // Валидно если: точное совпадение (score=2.0) или направление совпадает (score≥0.99).
            if (target is null || bestScore < 0.99)
            {
                SmartConLogger.Info($"[SmartCon] No valid match (bestScore={bestScore:F4} < 0.99)");
                return false;
            }

            // Логируем все параметры ConnectorElement для диагностики
            SmartConLogger.Info($"[SmartCon] All parameters on target ConnectorElement (Id={target.Id.Value}):");
            foreach (Parameter p in target.Parameters)
            {
                var name = p.Definition?.Name ?? "N/A";
                var isShared = p.IsShared ? "Shared" : "Built-in";
                SmartConLogger.Info($"[SmartCon]   Param: {name}, Type={isShared}, ReadOnly={p.IsReadOnly}, Value={p.AsValueString()}");
            }

            // Транзакция на family doc (отдельный документ — new Transaction допустим, I-03 не нарушается).
            using var familyTx = new Transaction(familyDoc, "SetConnectorDescription");
            familyTx.Start();

            // RBS_CONNECTOR_DESCRIPTION — единственный locale-независимый BIP для параметра
            // "Описание соединителя" / "Connector Description" на ConnectorElement.
            var descParam = target.get_Parameter(BuiltInParameter.RBS_CONNECTOR_DESCRIPTION);
            SmartConLogger.Info($"[SmartCon] descParam found: '{descParam?.Definition?.Name ?? "NULL"}', IsReadOnly={descParam?.IsReadOnly}");

            // Формат: "КОД.НАЗВАНИЕ.ОПИСАНИЕ" — читается через ConnectionTypeCode.Parse
            var value = $"{typeDef.Code}.{typeDef.Name}.{typeDef.Description}";

            bool success;
            if (descParam is not null && !descParam.IsReadOnly)
            {
                // Path 1: прямая запись (параметр доступен).
                SmartConLogger.Info($"[SmartCon] Path1: param='{descParam.Definition?.Name}', StorageType={descParam.StorageType}, valueBefore='{descParam.AsString()}'");
                bool setOk = descParam.Set(value);
                SmartConLogger.Info($"[SmartCon] Path1: Set()={setOk}, valueAfter='{descParam.AsString()}'");
                success = setOk;
            }
            else if (descParam is not null && descParam.IsReadOnly)
            {
                // Path 2: параметр ReadOnly из-за driving FamilyParameter.
                SmartConLogger.Info($"[SmartCon] Path2: '{descParam.Definition?.Name}' ReadOnly → trying FamilyManager");
                success = TrySetDrivingFamilyParameter(familyDoc.FamilyManager, target, descParam, value);
                if (!success)
                {
                    SmartConLogger.Info("[SmartCon] Path2 failed → trying SystemClassification = Global (Path3)");
                    success = TrySetViaSystemClassificationChange(familyDoc, target, value);
                }
            }
            else
            {
                // Path 3: descParam is null — параметр скрыт при SystemClassification = Fitting.
                // TrySetViaSystemClassificationChange переключит на Global, сделает Regenerate,
                // и параметр появится.
                SmartConLogger.Info("[SmartCon] Path3: descParam NULL (likely Fitting hides it) → switching to Global");
                success = TrySetViaSystemClassificationChange(familyDoc, target, value);
            }

            if (!success)
            {
                familyTx.RollBack();
                return false;
            }

            var txStatus = familyTx.Commit();
            SmartConLogger.Info($"[SmartCon] familyTx.Commit() status = {txStatus}");

            if (txStatus != Autodesk.Revit.DB.TransactionStatus.Committed)
            {
                SmartConLogger.Info($"[SmartCon] FATAL: transaction not committed, status={txStatus}, returning false");
                return false;
            }

            // Явно освобождаем familyTx чтобы familyDoc.IsModifiable стал false
            familyTx.Dispose();
            SmartConLogger.Info($"[SmartCon] familyTx disposed, familyDoc.IsModifiable = {familyDoc.IsModifiable}");

            if (familyDoc.IsModifiable)
            {
                SmartConLogger.Info("[SmartCon] WARNING: familyDoc.IsModifiable is still true after Dispose! LoadFamily may reload unchanged family.");
            }

            // Загружаем изменённое семейство обратно в проект.
            // LoadFamily требует familyDoc.IsModifiable = false (транзакция закрыта).
            SmartConLogger.Info("[SmartCon] Starting LoadFamily...");
            var loadedFamily = familyDoc.LoadFamily(doc, new FamilyLoadOptions());
            SmartConLogger.Info($"[SmartCon] LoadFamily result: {(loadedFamily is null ? "NULL (existing family kept)" : $"loaded '{loadedFamily.Name}'")}");

            return true;
        }
        finally
        {
            familyDoc.Close(false);
            SmartConLogger.Info("[SmartCon] familyDoc closed");
        }
    }

    /// <summary>
    /// Находит FamilyParameter, управляющий connParam на connElem (через AssociatedParameters),
    /// и устанавливает его значение через FamilyManager.Set для всех типоразмеров.
    /// Вызывать внутри открытой транзакции на familyDoc.
    /// </summary>
    private static bool TrySetDrivingFamilyParameter(
        FamilyManager fm, ConnectorElement connElem, Parameter connParam, string value)
    {
        FamilyParameter? drivingFp = null;

        foreach (FamilyParameter fp in fm.Parameters)
        {
            try
            {
                foreach (Parameter assoc in fp.AssociatedParameters)
                {
                    if (assoc.Id == connParam.Id && assoc.Element?.Id == connElem.Id)
                    {
                        drivingFp = fp;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                SmartConLogger.Info($"[SmartCon] AssociatedParameters error for '{fp.Definition?.Name}': {ex.Message}");
            }
            if (drivingFp is not null) break;
        }

        if (drivingFp is null)
        {
            SmartConLogger.Info("[SmartCon] TrySetDrivingFamilyParameter: no driving FamilyParameter found");
            return false;
        }

        if (!string.IsNullOrEmpty(drivingFp.Formula))
        {
            SmartConLogger.Info($"[SmartCon] TrySetDrivingFamilyParameter: '{drivingFp.Definition?.Name}' has formula '{drivingFp.Formula}' — cannot set");
            return false;
        }

        try
        {
            if (!drivingFp.IsInstance)
            {
                foreach (FamilyType ft in fm.Types)
                {
                    fm.CurrentType = ft;
                    fm.Set(drivingFp, value);
                }
            }
            else
            {
                fm.Set(drivingFp, value);
            }
            SmartConLogger.Info($"[SmartCon] TrySetDrivingFamilyParameter: set '{drivingFp.Definition?.Name}' = '{value}' (isInstance={drivingFp.IsInstance})");
            return true;
        }
        catch (Exception ex)
        {
            SmartConLogger.Info($"[SmartCon] TrySetDrivingFamilyParameter failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Path 3: если Описание соединителя ReadOnly из-за SystemClassification = Fitting,
    /// временно меняет SystemClassification на Global (разблокирует параметр),
    /// записывает value, восстанавливает исходный SystemClassification.
    /// Вызывать внутри открытой транзакции на familyDoc.
    /// </summary>
    private static bool TrySetViaSystemClassificationChange(Document familyDoc, ConnectorElement target, string value)
    {
        // Ищем параметр системной классификации по имени (локаль-независимо).
        Parameter? sysParam = null;
        foreach (Parameter p in target.Parameters)
        {
            var name = p.Definition?.Name ?? "";
            if (string.Equals(name, "System Classification", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "Классификация систем", StringComparison.OrdinalIgnoreCase))
            {
                sysParam = p;
                break;
            }
        }

        if (sysParam is null)
        {
            SmartConLogger.Info("[SmartCon] TrySetViaSystemClassificationChange: 'System Classification' param not found");
            return false;
        }

        if (sysParam.IsReadOnly)
        {
            SmartConLogger.Info("[SmartCon] TrySetViaSystemClassificationChange: SystemClassification is ReadOnly, cannot change");
            return false;
        }

        int originalSysClass = sysParam.AsInteger();
        int globalValue = (int)PipeSystemType.Global;
        SmartConLogger.Info($"[SmartCon] TrySetViaSystemClassificationChange: sysClass {originalSysClass} → {globalValue} (Global)");

        try
        {
            sysParam.Set(globalValue);

            // Regenerate нужен чтобы Revit снял ReadOnly с параметра описания после смены классификации.
            familyDoc.Regenerate();

            // После смены на Global — переищем параметр описания (IsReadOnly мог измениться).
            var descParam = target.get_Parameter(BuiltInParameter.RBS_CONNECTOR_DESCRIPTION);

            SmartConLogger.Info($"[SmartCon] TrySetViaSystemClassificationChange: descParam after Global='{descParam?.Definition?.Name ?? "NULL"}', IsReadOnly={descParam?.IsReadOnly}");

            if (descParam is null || descParam.IsReadOnly)
            {
                SmartConLogger.Info("[SmartCon] TrySetViaSystemClassificationChange: Description still NULL/ReadOnly after Global switch");
                sysParam.Set(originalSysClass);
                return false;
            }

            bool setOk = descParam.Set(value);
            SmartConLogger.Info($"[SmartCon] TrySetViaSystemClassificationChange: Set()={setOk}, valueAfter='{descParam.AsString()}'");
            sysParam.Set(originalSysClass);
            SmartConLogger.Info($"[SmartCon] TrySetViaSystemClassificationChange: SystemClassification restored to {originalSysClass}");
            return setOk;
        }
        catch (Exception ex)
        {
            SmartConLogger.Info($"[SmartCon] TrySetViaSystemClassificationChange failed: {ex.Message}");
            try { sysParam.Set(originalSysClass); } catch (Exception restoreEx) { SmartConLogger.Debug($"[Restore SystemClassification] {restoreEx.GetType().Name}: {restoreEx.Message}"); }
            return false;
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
