using System;
using System.Collections.Generic;
using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Math;
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
        var element = doc.GetElement(elementId);
        if (element is null) return [];

        // MEP Curve (Pipe, Duct, FlexPipe) → фиксированный built-in параметр
        if (element is MEPCurve or FlexPipe)
        {
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
        if (element is not FamilyInstance instance) return [];

        var cm        = instance.MEPModel?.ConnectorManager;
        var connector = cm?.FindByIndex(connectorIndex);
        if (connector is null) return [];

        var mepInfo = connector.GetMEPConnectorInfo() as MEPFamilyConnectorInfo;
        if (mepInfo is null) return [];

        // Определяем, к какому FamilyParameter привязан CONNECTOR_RADIUS / CONNECTOR_DIAMETER
        var radiusParamId = mepInfo.GetAssociateFamilyParameterId(new ElementId(BuiltInParameter.CONNECTOR_RADIUS));
        var diamParamId   = mepInfo.GetAssociateFamilyParameterId(new ElementId(BuiltInParameter.CONNECTOR_DIAMETER));

        bool useRadius   = radiusParamId.Value > 0;
        bool useDiameter = !useRadius && diamParamId.Value > 0;

        if (!useRadius && !useDiameter)
        {
            Debug.WriteLine("[SmartCon][Resolver] GetMEPConnectorInfo: нет привязанного параметра к CONNECTOR_RADIUS/DIAMETER");
            return [];
        }

        var activeParamId = useRadius ? radiusParamId : diamParamId;
        bool isDiameter   = useDiameter;

        // Получаем имя параметра через элемент в doc (ParameterElement)
        var familyParamElem = doc.GetElement(activeParamId);
        var paramName = familyParamElem?.Name;
        if (string.IsNullOrEmpty(paramName))
        {
            Debug.WriteLine("[SmartCon][Resolver] GetMEPConnectorInfo: не удалось получить имя параметра");
            return [];
        }

        Debug.WriteLine($"[SmartCon][Resolver] GetMEPConnectorInfo: paramName={paramName}, isDiameter={isDiameter}");

        // Проверяем: параметр экземпляра или типа?
        var instParam = element.LookupParameter(paramName);
        bool isInstance = instParam is not null;
        bool isReadOnly = instParam?.IsReadOnly ?? false;

        // Параметр экземпляра, записываемый → прямая запись
        if (isInstance && !isReadOnly)
        {
            var dep = new ParameterDependency(
                BuiltIn: null,
                SharedParamName: null,
                Formula: null,
                IsInstance: true,
                DirectParamName: paramName,
                RootParamName: null,
                IsDiameter: isDiameter);
            Cache(elementId, connectorIndex, dep);
            return [dep];
        }

        // Параметр экземпляра ReadOnly → есть формула → пытаемся EditFamily для её анализа
        if (isInstance && isReadOnly && !doc.IsModifiable)
        {
            var family = instance.Symbol?.Family;
            if (family is not null)
            {
                Document? familyDoc = null;
                try
                {
                    familyDoc = doc.EditFamily(family);
                    var (directName, rootName, formula, _) =
                        FamilyParameterAnalyzer.AnalyzeConnectorRadiusParam(
                            familyDoc, instance.GetTransform(), connector.CoordinateSystem.Origin);

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
                        return [dep];
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SmartCon][Resolver] EditFamily (formula fallback) failed: {ex.Message}");
                }
                finally
                {
                    familyDoc?.Close(false);
                }
            }
        }

        // Параметр типа (не на экземпляре) ИЛИ формула не разрешена → ChangeTypeId
        {
            var dep = new ParameterDependency(
                BuiltIn: null,
                SharedParamName: null,
                Formula: null,
                IsInstance: false,
                DirectParamName: paramName,
                RootParamName: null,
                IsDiameter: isDiameter);
            Cache(elementId, connectorIndex, dep);
            return [dep];
        }
    }

    public bool TrySetConnectorRadius(Document doc, ElementId elementId,
        int connectorIndex, double targetRadiusInternalUnits)
    {
        var element = doc.GetElement(elementId);
        if (element is null) return false;

        // ── MEP Curve: прямая запись диаметра ────────────────────────────
        if (element is MEPCurve or FlexPipe)
        {
            var diamParam = element.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
            if (diamParam is null || diamParam.IsReadOnly) return false;
            diamParam.Set(targetRadiusInternalUnits * 2.0);
            return true;
        }

        if (element is not FamilyInstance) return false;

        // ── FamilyInstance: использовать кешированные deps ───────────────
        _cache.TryGetValue((elementId.Value, connectorIndex), out var dep);

        if (dep is not null)
        {
            // Если параметр хранит диаметр — цель = radius * 2
            double targetValue = dep.IsDiameter
                ? targetRadiusInternalUnits * 2.0
                : targetRadiusInternalUnits;

            // Параметр экземпляра без формулы → прямая запись
            if (dep.IsInstance && dep.Formula is null && dep.DirectParamName is not null)
            {
                var param = element.LookupParameter(dep.DirectParamName);
                if (param is not null && !param.IsReadOnly)
                {
                    param.Set(targetValue);
                    return true;
                }
            }

            // Параметр экземпляра с формулой → SolveFor → запись корневого
            if (dep.IsInstance && dep.Formula is not null && dep.RootParamName is not null)
            {
                var value = MiniFormulaSolver.SolveFor(
                    dep.Formula, dep.RootParamName, targetValue);
                if (value is not null)
                {
                    var param = element.LookupParameter(dep.RootParamName);
                    if (param is not null && !param.IsReadOnly)
                    {
                        param.Set(value.Value);
                        return true;
                    }
                }
                // Формула нелинейна или параметр ReadOnly → переходим к TryChangeTypeTo
            }
        }

        // ── Смена типоразмера: SubTransaction-перебор ─────────────────────
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
        var element = doc.GetElement(elementId) as FamilyInstance;
        if (element is null) return false;

        RevitFamily? family = element.Symbol?.Family;
        if (family is null) return false;

        var symbolIds = family.GetFamilySymbolIds();
        ElementId? bestSymbolId = null;
        double     bestDelta    = double.MaxValue;

        foreach (var symbolId in symbolIds)
        {
            using var st = new SubTransaction(doc);
            try
            {
                st.Start();

                // Перезачитываем элемент после SubTransaction.Start
                var inst = doc.GetElement(elementId) as FamilyInstance;
                if (inst is null) { st.RollBack(); continue; }

                inst.ChangeTypeId(symbolId);

                // Перечитать коннектор после смены типа
                var cm   = inst.MEPModel?.ConnectorManager;
                var conn = cm?.FindByIndex(connectorIndex);
                if (conn is null) { st.RollBack(); continue; }

                double newRadius = conn.Radius;
                double delta     = System.Math.Abs(newRadius - targetRadius);

                if (delta < Epsilon)
                {
                    st.Commit();
                    Debug.WriteLine($"[SmartCon][Resolver] ChangeTypeId exact match symbolId={symbolId.Value}, radius={newRadius:F6}");
                    return true;
                }

                if (delta < bestDelta)
                {
                    bestDelta    = delta;
                    bestSymbolId = symbolId;
                }

                st.RollBack();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SmartCon][Resolver] ChangeTypeId failed for symbolId={symbolId.Value}: {ex.Message}");
                try { st.RollBack(); } catch { /* ignored */ }
            }
        }

        // Точного совпадения нет — применяем ближайший (без SubTransaction, постоянно)
        if (bestSymbolId is not null)
        {
            try
            {
                var inst = doc.GetElement(elementId) as FamilyInstance;
                inst?.ChangeTypeId(bestSymbolId);
                Debug.WriteLine($"[SmartCon][Resolver] ChangeTypeId nearest match symbolId={bestSymbolId.Value}, delta={bestDelta:F6}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SmartCon][Resolver] ChangeTypeId nearest failed: {ex.Message}");
            }
            return false; // false = только ближайший (вызывающий выставит NeedsAdapter)
        }

        return false;
    }

    private void Cache(ElementId elementId, int connectorIndex, ParameterDependency dep)
        => _cache[(elementId.Value, connectorIndex)] = dep;
}
