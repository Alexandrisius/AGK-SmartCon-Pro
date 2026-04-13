using System;
using System.Collections.Generic;
using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Math;
using SmartCon.Core.Math.FormulaEngine.Solver;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Extensions;
using RevitFamily = Autodesk.Revit.DB.Family;

namespace SmartCon.Revit.Parameters;

/// <summary>
/// Реализация IParameterResolver через Revit API.
///
/// GetConnectorRadiusDependencies:
///   — MEP Curve → RBS_PIPE_DIAMETER_PARAM (прямая запись, нет EditFamily)
///   — FamilyInstance → EditFamily + FamilyParameterAnalyzer (ВОВНЕ транзакции)
///
/// TrySetConnectorRadius:
///   — MEP Curve → прямая запись RBS_PIPE_DIAMETER_PARAM (внутри транзакции)
///   — FamilyInstance → использует кешированные deps, SubTransaction для смены типа
///
/// Кеш deps хранится per-operation между GetConnectorRadiusDependencies и TrySetConnectorRadius.
/// </summary>
public sealed class RevitParameterResolver : IParameterResolver
{
    private const double Epsilon = 1e-6; // ~0.3 мкм в футах

    // Кеш результатов GetConnectorRadiusDependencies в рамках одной операции
    private readonly Dictionary<(long ElementId, int ConnectorIndex), ParameterDependency> _cache = new();

    // ── IParameterResolver ────────────────────────────────────────────────

    public IReadOnlyList<ParameterDependency> GetConnectorRadiusDependencies(
        Document doc, ElementId elementId, int connectorIndex)
    {
        SmartConLogger.LookupSection("GetConnectorRadiusDependencies");
        SmartConLogger.Lookup($"  elementId={elementId.Value}, connIdx={connectorIndex}");

        var element = doc.GetElement(elementId);
        if (element is null)
        {
            SmartConLogger.Lookup("  element=null → return []");
            return [];
        }

        SmartConLogger.Lookup($"  element='{element.Name}' ({element.GetType().Name})");

        // MEP Curve (Pipe, Duct, FlexPipe) → фиксированный built-in параметр
        if (element is MEPCurve or FlexPipe)
        {
            SmartConLogger.Lookup("  → MEPCurve/FlexPipe: RBS_PIPE_DIAMETER_PARAM (прямая запись)");
            var dep = new ParameterDependency(
                BuiltIn: BuiltInParameter.RBS_PIPE_DIAMETER_PARAM,
                SharedParamName: null,
                Formula: null,
                IsInstance: true,
                DirectParamName: null,
                RootParamName: null);
            Cache(elementId, connectorIndex, dep);
            return [dep];
        }

        // FamilyInstance → GetMEPConnectorInfo (надёжный прямой API, без EditFamily)
        if (element is not FamilyInstance instance)
        {
            SmartConLogger.Lookup($"  not FamilyInstance ({element.GetType().Name}) → return []");
            return [];
        }

        var cm        = instance.MEPModel?.ConnectorManager;
        var connector = cm?.FindByIndex(connectorIndex);
        if (connector is null)
        {
            SmartConLogger.Lookup($"  connector[{connectorIndex}]=null (ConnectorManager: {(cm is null ? "null" : $"{cm.Connectors.Size} conn")}) → return []");
            return [];
        }

        SmartConLogger.Lookup($"  connector[{connectorIndex}] найден, Radius={connector.Radius:F6} ft ({connector.Radius * 304.8:F2} mm)");

        var mepInfo = connector.GetMEPConnectorInfo() as MEPFamilyConnectorInfo;
        if (mepInfo is null)
        {
            SmartConLogger.Lookup("  GetMEPConnectorInfo()=null (не MEPFamilyConnectorInfo) → return []");
            return [];
        }

        SmartConLogger.Lookup("  MEPFamilyConnectorInfo получен");

        // Определяем, к какому FamilyParameter привязан CONNECTOR_RADIUS / CONNECTOR_DIAMETER
        var radiusParamId = mepInfo.GetAssociateFamilyParameterId(new ElementId(BuiltInParameter.CONNECTOR_RADIUS));
        var diamParamId   = mepInfo.GetAssociateFamilyParameterId(new ElementId(BuiltInParameter.CONNECTOR_DIAMETER));

        SmartConLogger.Lookup($"  GetAssociateFamilyParameterId: CONNECTOR_RADIUS → id={radiusParamId.Value}, CONNECTOR_DIAMETER → id={diamParamId.Value}");

        bool useRadius   = radiusParamId.Value > 0;
        bool useDiameter = !useRadius && diamParamId.Value > 0;

        if (!useRadius && !useDiameter)
        {
            SmartConLogger.Lookup("  ВНИМАНИЕ: нет привязанного параметра к CONNECTOR_RADIUS/DIAMETER → return []");
            SmartConLogger.Warn($"[Resolver] elementId={elementId.Value}: нет привязки CONNECTOR_RADIUS или CONNECTOR_DIAMETER");
            return [];
        }

        var activeParamId = useRadius ? radiusParamId : diamParamId;
        bool isDiameter   = useDiameter;
        SmartConLogger.Lookup($"  Используем: {(useRadius ? "CONNECTOR_RADIUS" : "CONNECTOR_DIAMETER")}, activeParamId={activeParamId.Value}, isDiameter={isDiameter}");

        // Получаем имя параметра через элемент в doc (ParameterElement)
        var familyParamElem = doc.GetElement(activeParamId);
        var paramName = familyParamElem?.Name;
        SmartConLogger.Lookup($"  ParameterElement.Name='{paramName}' (elementType={familyParamElem?.GetType().Name})");

        if (string.IsNullOrEmpty(paramName))
        {
            SmartConLogger.Lookup("  ВНИМАНИЕ: не удалось получить имя параметра из ParameterElement → return []");
            SmartConLogger.Warn($"[Resolver] elementId={elementId.Value}: имя параметра пустое");
            return [];
        }

        // Проверяем: параметр экземпляра или типа?
        var instParam = element.LookupParameter(paramName);
        bool isInstance = instParam is not null;
        bool isReadOnly = instParam?.IsReadOnly ?? false;

        SmartConLogger.Lookup($"  LookupParameter('{paramName}'): isInstance={isInstance}, isReadOnly={isReadOnly}");

        // Параметр экземпляра, записываемый → прямая запись
        if (isInstance && !isReadOnly)
        {
            SmartConLogger.Lookup($"  → Прямой параметр экземпляра: '{paramName}', isDiameter={isDiameter}");
            var dep = new ParameterDependency(
                BuiltIn: null,
                SharedParamName: null,
                Formula: null,
                IsInstance: true,
                DirectParamName: paramName,
                RootParamName: null,
                IsDiameter: isDiameter);
            Cache(elementId, connectorIndex, dep);
            SmartConLogger.Lookup($"  → dep: IsInstance=true, DirectParamName='{paramName}', IsDiameter={isDiameter}");
            return [dep];
        }

        // Параметр экземпляра ReadOnly → есть формула → пытаемся EditFamily для её анализа
        if (isInstance && isReadOnly && !doc.IsModifiable)
        {
            SmartConLogger.Lookup($"  → Параметр ReadOnly, пробуем EditFamily для анализа формулы...");
            var family = instance.Symbol?.Family;
            if (family is not null)
            {
                Document? familyDoc = null;
                try
                {
                    SmartConLogger.Lookup($"  → EditFamily('{family.Name}')...");
                    familyDoc = doc.EditFamily(family);
                    var (directName, rootName, formula, _, _) =
                        FamilyParameterAnalyzer.AnalyzeConnectorRadiusParam(
                            familyDoc, instance.GetTransform(), connector.CoordinateSystem.Origin,
                            instance.HandFlipped, instance.FacingFlipped);

                    SmartConLogger.Lookup($"  FPA: directName='{directName}', rootName='{rootName}', formula='{formula}'");

                    if (directName is not null && formula is not null && rootName is not null)
                    {
                        var dep = new ParameterDependency(
                            BuiltIn: null,
                            SharedParamName: null,
                            Formula: formula,
                            IsInstance: true,
                            DirectParamName: directName,
                            RootParamName: rootName,
                            IsDiameter: isDiameter);
                        Cache(elementId, connectorIndex, dep);
                        SmartConLogger.Lookup($"  → dep с формулой: DirectParamName='{directName}', RootParamName='{rootName}', Formula='{formula}'");
                        return [dep];
                    }
                    else
                    {
                        SmartConLogger.Lookup("  FPA не вернул полную цепочку (directName/formula/rootName null)");
                    }
                }
                catch (Exception ex)
                {
                    SmartConLogger.Lookup($"  ИСКЛЮЧЕНИЕ EditFamily (formula fallback): {ex.GetType().Name}: {ex.Message}");
                    SmartConLogger.Warn($"[Resolver] EditFamily fallback failed: {ex.Message}");
                }
                finally
                {
                    familyDoc?.Close(false);
                }
            }
            else
            {
                SmartConLogger.Lookup("  family=null (Symbol или Family не найдено), EditFamily пропущен");
            }
        }
        else if (isInstance && isReadOnly && doc.IsModifiable)
        {
            SmartConLogger.Lookup("  ВНИМАНИЕ: параметр ReadOnly но doc.IsModifiable=true — EditFamily пропущен");
        }

        // Параметр типа (не на экземпляре) ИЛИ формула не разрешена → ChangeTypeId
        {
            SmartConLogger.Lookup($"  → Параметр типа / ReadOnly без формулы: ChangeTypeId будет использован");
            SmartConLogger.Lookup($"    paramName='{paramName}', isInstance={isInstance}, isReadOnly={isReadOnly}, isDiameter={isDiameter}");
            var dep = new ParameterDependency(
                BuiltIn: null,
                SharedParamName: null,
                Formula: null,
                IsInstance: false,
                DirectParamName: paramName,
                RootParamName: null,
                IsDiameter: isDiameter);
            Cache(elementId, connectorIndex, dep);
            SmartConLogger.Lookup($"  → dep: IsInstance=false (ChangeTypeId), DirectParamName='{paramName}'");
            return [dep];
        }
    }

    public bool TrySetConnectorRadius(Document doc, ElementId elementId,
        int connectorIndex, double targetRadiusInternalUnits)
    {
        SmartConLogger.LookupSection("TrySetConnectorRadius");
        SmartConLogger.Lookup($"  elementId={elementId.Value}, connIdx={connectorIndex}, targetRadius={targetRadiusInternalUnits:F6} ft ({targetRadiusInternalUnits * 304.8:F2} mm)");

        var element = doc.GetElement(elementId);
        if (element is null)
        {
            SmartConLogger.Lookup("  element=null → return false");
            return false;
        }

        // ── MEP Curve: прямая запись диаметра ────────────────────────────
        if (element is MEPCurve or FlexPipe)
        {
            var diamParam = element.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
            if (diamParam is null || diamParam.IsReadOnly)
            {
                SmartConLogger.Lookup("  MEPCurve: RBS_PIPE_DIAMETER_PARAM=null или ReadOnly → return false");
                return false;
            }
            double diamVal = targetRadiusInternalUnits * 2.0;
            SmartConLogger.Lookup($"  MEPCurve: RBS_PIPE_DIAMETER_PARAM.Set({diamVal:F6} ft = {diamVal * 304.8:F2} mm)");
            diamParam.Set(diamVal);
            SmartConLogger.Lookup("  → return true");
            return true;
        }

        if (element is not FamilyInstance)
        {
            SmartConLogger.Lookup($"  not FamilyInstance → return false");
            return false;
        }

        // ── FamilyInstance: использовать кешированные deps ───────────────
        _cache.TryGetValue((elementId.Value, connectorIndex), out var dep);
        SmartConLogger.Lookup($"  Кеш dep: {(dep is null ? "null (нет кеша)" : $"IsInstance={dep.IsInstance}, Formula='{dep.Formula}', DirectParamName='{dep.DirectParamName}', RootParamName='{dep.RootParamName}', IsDiameter={dep.IsDiameter}")}");

        if (dep is not null)
        {
            // Если параметр хранит диаметр — цель = radius * 2
            double targetValue = dep.IsDiameter
                ? targetRadiusInternalUnits * 2.0
                : targetRadiusInternalUnits;
            SmartConLogger.Lookup($"  targetValue={targetValue:F6} ft (IsDiameter={dep.IsDiameter}, radius={targetRadiusInternalUnits:F6})");

            // Параметр экземпляра без формулы → прямая запись
            if (dep.IsInstance && dep.Formula is null && dep.DirectParamName is not null)
            {
                var param = element.LookupParameter(dep.DirectParamName);
                SmartConLogger.Lookup($"  LookupParameter('{dep.DirectParamName}'): {(param is null ? "null" : $"IsReadOnly={param.IsReadOnly}, StorageType={param.StorageType}")}");
                if (param is not null && !param.IsReadOnly)
                {
                    param.Set(targetValue);
                    SmartConLogger.Lookup($"  → Прямая запись '{dep.DirectParamName}'={targetValue:F6} → return true");
                    return true;
                }
                SmartConLogger.Lookup($"  → Прямая запись не удалась (null или ReadOnly)");
            }

            // Параметр экземпляра с формулой → SolveFor → запись корневого
            if (dep.IsInstance && dep.Formula is not null && dep.RootParamName is not null)
            {
                SmartConLogger.Lookup($"  SolveFor('{dep.Formula}', '{dep.RootParamName}', {targetValue:F6})...");
                var value = FormulaSolver.SolveForStatic(
                    dep.Formula, dep.RootParamName, targetValue);
                SmartConLogger.Lookup($"  SolveFor результат: {(value is null ? "null (нелинейная/не поддерживается)" : $"{value.Value:F6}")}");
                if (value is not null)
                {
                    var param = element.LookupParameter(dep.RootParamName);
                    SmartConLogger.Lookup($"  LookupParameter('{dep.RootParamName}'): {(param is null ? "null" : $"IsReadOnly={param.IsReadOnly}")}");
                    if (param is not null && !param.IsReadOnly)
                    {
                        param.Set(value.Value);
                        SmartConLogger.Lookup($"  → Запись через формулу '{dep.RootParamName}'={value.Value:F6} → return true");
                        return true;
                    }
                }
                SmartConLogger.Lookup("  → SolveFor не дал результата или ReadOnly → return false (dep.IsInstance=True, тип не меняем)");
                return false;
            }

            // dep.IsInstance=True, но ни одна ветка не вернула (прямая запись не удалась, формулы нет)
            if (dep.IsInstance)
            {
                // Fallback: прямая запись не сработала → пробуем сменить типоразмер.
                // У многих фитингов IsInstance=true, но допустимые значения
                // определяются lookup-таблицей типоразмера.
                SmartConLogger.Lookup("  dep.IsInstance=True: fallback → TryChangeTypeTo...");
                return TryChangeTypeTo(doc, elementId, connectorIndex, targetRadiusInternalUnits);
            }
        }
        else
        {
            SmartConLogger.Lookup("  dep=null в кеше → переходим к TryChangeTypeTo");
        }

        // dep=null или dep.IsInstance=False → параметр типа → смена типоразмера
        // (если dep.IsInstance=True — все ветки выше должны были вернуть, сюда не доходим)
        SmartConLogger.Lookup("  → TryChangeTypeTo (параметр типа)...");
        return TryChangeTypeTo(doc, elementId, connectorIndex, targetRadiusInternalUnits);
    }

    // ── Вспомогательные ───────────────────────────────────────────────────

    /// <summary>
    /// Перебирает все FamilySymbol семейства через SubTransaction,
    /// проверяет radius коннектора после каждой смены.
    /// При точном совпадении — Commit, при приближённом — применяет ближайший и возвращает false.
    /// </summary>
    private bool TryChangeTypeTo(Document doc, ElementId elementId,
        int connectorIndex, double targetRadius)
    {
        SmartConLogger.LookupSection("TryChangeTypeTo");
        SmartConLogger.Lookup($"  elementId={elementId.Value}, connIdx={connectorIndex}, targetRadius={targetRadius:F6} ft ({targetRadius * 304.8:F2} mm)");

        var element = doc.GetElement(elementId) as FamilyInstance;
        if (element is null)
        {
            SmartConLogger.Lookup("  element=null → return false");
            return false;
        }

        RevitFamily? family = element.Symbol?.Family;
        if (family is null)
        {
            SmartConLogger.Lookup("  family=null → return false");
            return false;
        }

        var symbolIds = family.GetFamilySymbolIds().ToList();
        SmartConLogger.Lookup($"  Семейство '{family.Name}': {symbolIds.Count} типоразмеров");

        ElementId? bestSymbolId = null;
        double     bestDelta    = double.MaxValue;
        string?    bestSymbolName = null;

        foreach (var symbolId in symbolIds)
        {
            using var st = new SubTransaction(doc);
            try
            {
                st.Start();

                // Перезачитываем элемент после SubTransaction.Start
                var inst = doc.GetElement(elementId) as FamilyInstance;
                if (inst is null) { st.RollBack(); continue; }

                var symbolElem = doc.GetElement(symbolId) as FamilySymbol;
                SmartConLogger.Lookup($"  Пробуем symbolId={symbolId.Value} ('{symbolElem?.Name}')...");

                inst.ChangeTypeId(symbolId);
                doc.Regenerate();

                // Перечитать коннектор после смены типа
                var cm   = inst.MEPModel?.ConnectorManager;
                var conn = cm?.FindByIndex(connectorIndex);
                if (conn is null)
                {
                    SmartConLogger.Lookup($"    connector[{connectorIndex}]=null после смены типа — RollBack");
                    st.RollBack();
                    continue;
                }

                double newRadius = conn.Radius;
                double delta     = System.Math.Abs(newRadius - targetRadius);

                SmartConLogger.Lookup($"    newRadius={newRadius:F6} ft ({newRadius * 304.8:F2} mm), delta={delta:F6}");

                if (delta < Epsilon)
                {
                    st.Commit();
                    SmartConLogger.Lookup($"    → ТОЧНОЕ совпадение! Commit. symbolId={symbolId.Value} ('{symbolElem?.Name}') → return true");
                    return true;
                }

                if (delta < bestDelta)
                {
                    bestDelta      = delta;
                    bestSymbolId   = symbolId;
                    bestSymbolName = symbolElem?.Name;
                }

                st.RollBack();
                SmartConLogger.Lookup($"    → не точный, RollBack");
            }
            catch (Exception ex)
            {
                SmartConLogger.Lookup($"  ИСКЛЮЧЕНИЕ для symbolId={symbolId.Value}: {ex.GetType().Name}: {ex.Message}");
                SmartConLogger.Warn($"[Resolver] ChangeTypeId symbolId={symbolId.Value} failed: {ex.Message}");
                try { st.RollBack(); } catch { /* ignored */ }
            }
        }

        // Точного совпадения нет — применяем ближайший (без SubTransaction, постоянно)
        if (bestSymbolId is not null)
        {
            SmartConLogger.Lookup($"  → Ближайший: symbolId={bestSymbolId.Value} ('{bestSymbolName}'), delta={bestDelta:F6} ft ({bestDelta * 304.8:F2} mm)");

            var currentSymbolId = (doc.GetElement(elementId) as FamilyInstance)?.Symbol?.Id;
            if (bestSymbolId == currentSymbolId)
            {
                SmartConLogger.Lookup($"  → Ближайший совпадает с текущим типом — пропуск ChangeTypeId (предотвращает сброс instance-параметров)");
                return false;
            }

            try
            {
                var inst = doc.GetElement(elementId) as FamilyInstance;
                inst?.ChangeTypeId(bestSymbolId);
                SmartConLogger.Lookup($"  → ChangeTypeId ближайшего выполнен → return false (NeedsAdapter)");
            }
            catch (Exception ex)
            {
                SmartConLogger.Lookup($"  ИСКЛЮЧЕНИЕ ChangeTypeId ближайшего: {ex.Message}");
                SmartConLogger.Warn($"[Resolver] ChangeTypeId nearest failed: {ex.Message}");
            }
            return false;
        }

        SmartConLogger.Lookup("  → Ни одного подходящего типоразмера не найдено → return false");
        return false;
    }

    public (bool StaticExact, double AchievedDynRadius) TrySetFittingTypeForPair(
        Document doc, ElementId fittingId,
        int staticConnIdx, double staticRadius,
        int dynConnIdx,    double dynRadius)
    {
        SmartConLogger.LookupSection("TrySetFittingTypeForPair");
        SmartConLogger.Lookup($"  fittingId={fittingId.Value}, staticConn={staticConnIdx} R={staticRadius:F6} ft ({staticRadius * 304.8:F2}mm), dynConn={dynConnIdx} R={dynRadius:F6} ft ({dynRadius * 304.8:F2}mm)");

        var element = doc.GetElement(fittingId) as FamilyInstance;
        if (element is null)
        {
            SmartConLogger.Lookup("  element=null → return (false, dynRadius)");
            return (false, dynRadius);
        }

        var family = element.Symbol?.Family;
        if (family is null)
        {
            SmartConLogger.Lookup("  family=null → return (false, dynRadius)");
            return (false, dynRadius);
        }

        var symbolIds = family.GetFamilySymbolIds().ToList();
        SmartConLogger.Lookup($"  Семейство '{family.Name}': {symbolIds.Count} типоразмеров");

        // Лучший кандидат среди типов с точным совпадением static-коннектора
        ElementId? bestExactId        = null;
        double     bestExactDynDelta  = double.MaxValue;
        double     bestExactDynRadius = dynRadius;

        // Лучший кандидат среди всех типов (fallback — минимальное отклонение по static)
        ElementId? bestFallbackId          = null;
        double     bestFallbackStaticDelta = double.MaxValue;
        double     bestFallbackDynRadius   = dynRadius;

        foreach (var symbolId in symbolIds)
        {
            using var st = new SubTransaction(doc);
            try
            {
                st.Start();

                var inst = doc.GetElement(fittingId) as FamilyInstance;
                if (inst is null) { st.RollBack(); continue; }

                var sym = doc.GetElement(symbolId) as FamilySymbol;
                SmartConLogger.Lookup($"  Пробуем symbolId={symbolId.Value} ('{sym?.Name}')...");

                inst.ChangeTypeId(symbolId);
                doc.Regenerate();

                var cm         = inst.MEPModel?.ConnectorManager;
                var staticConn = cm?.FindByIndex(staticConnIdx);
                var dynConn    = cm?.FindByIndex(dynConnIdx);

                if (staticConn is null || dynConn is null)
                {
                    SmartConLogger.Lookup($"    conn=null после смены типа — RollBack");
                    st.RollBack();
                    continue;
                }

                double staticDelta = System.Math.Abs(staticConn.Radius - staticRadius);
                double dynDelta    = System.Math.Abs(dynConn.Radius    - dynRadius);
                SmartConLogger.Lookup($"    staticR={staticConn.Radius:F6} (Δ={staticDelta:F6}), dynR={dynConn.Radius:F6} (Δ={dynDelta:F6})");

                if (staticDelta < Epsilon)
                {
                    if (dynDelta < bestExactDynDelta)
                    {
                        bestExactDynDelta  = dynDelta;
                        bestExactId        = symbolId;
                        bestExactDynRadius = dynConn.Radius;
                        SmartConLogger.Lookup($"    → Новый лучший (static exact): dynΔ={dynDelta:F6}");
                    }
                }

                if (staticDelta < bestFallbackStaticDelta)
                {
                    bestFallbackStaticDelta = staticDelta;
                    bestFallbackId          = symbolId;
                    bestFallbackDynRadius   = dynConn.Radius;
                    SmartConLogger.Lookup($"    → Новый лучший fallback: staticΔ={staticDelta:F6}");
                }

                st.RollBack();
            }
            catch (Exception ex)
            {
                SmartConLogger.Lookup($"  ИСКЛЮЧЕНИЕ для symbolId={symbolId.Value}: {ex.Message}");
                SmartConLogger.Warn($"[TrySetFittingTypeForPair] symbolId={symbolId.Value}: {ex.Message}");
                try { st.RollBack(); } catch { /* ignored */ }
            }
        }

        bool       staticExact  = bestExactId is not null;
        ElementId? winnerId     = bestExactId ?? bestFallbackId;
        double     achievedDynR = staticExact ? bestExactDynRadius : bestFallbackDynRadius;

        SmartConLogger.Lookup($"  Победитель: staticExact={staticExact}, symbolId={winnerId?.Value}, achievedDynR={achievedDynR:F6} ft ({achievedDynR * 304.8:F2}mm)");

        if (winnerId is not null)
        {
            try
            {
                var inst = doc.GetElement(fittingId) as FamilyInstance;
                if (inst is not null)
                {
                    inst.ChangeTypeId(winnerId);
                    SmartConLogger.Lookup($"  → ChangeTypeId({winnerId.Value}) применён");
                }
            }
            catch (Exception ex)
            {
                SmartConLogger.Lookup($"  ИСКЛЮЧЕНИЕ при применении победителя: {ex.Message}");
                SmartConLogger.Warn($"[TrySetFittingTypeForPair] ChangeTypeId winner failed: {ex.Message}");
            }
        }
        else
        {
            SmartConLogger.Lookup("  → Ни одного подходящего типоразмера не найдено");
        }

        return (staticExact, achievedDynR);
    }

    private void Cache(ElementId elementId, int connectorIndex, ParameterDependency dep)
        => _cache[(elementId.Value, connectorIndex)] = dep;
}
