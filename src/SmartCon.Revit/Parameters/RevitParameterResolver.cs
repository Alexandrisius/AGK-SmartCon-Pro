using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Math.FormulaEngine.Solver;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Extensions;
using SmartCon.Core;
using SmartCon.Core.Compatibility;


using static SmartCon.Core.Units;
namespace SmartCon.Revit.Parameters;

public sealed class RevitParameterResolver : IParameterResolver
{
    private const double Epsilon = 1e-6;

    private readonly Dictionary<(long ElementId, int ConnectorIndex), ParameterDependency> _cache = new();

    public IReadOnlyList<ParameterDependency> GetConnectorRadiusDependencies(
        Document doc, ElementId elementId, int connectorIndex)
    {
        SmartConLogger.DebugSection("GetConnectorRadiusDependencies");
        SmartConLogger.Debug($"  elementId={elementId.GetValue()}, connIdx={connectorIndex}");

        var element = doc.GetElement(elementId);
        if (element is null)
        {
            SmartConLogger.Debug("  element=null → return []");
            return [];
        }

        SmartConLogger.Debug($"  element='{element.Name}' ({element.GetType().Name})");

        if (element is MEPCurve or FlexPipe)
        {
            SmartConLogger.Debug("  → MEPCurve/FlexPipe: RBS_PIPE_DIAMETER_PARAM (direct write)");
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

        if (element is not FamilyInstance instance)
        {
            SmartConLogger.Debug($"  not FamilyInstance ({element.GetType().Name}) → return []");
            return [];
        }

        var cm = instance.MEPModel?.ConnectorManager;
        var connector = cm?.FindByIndex(connectorIndex);
        if (connector is null)
        {
            SmartConLogger.Debug($"  connector[{connectorIndex}]=null (ConnectorManager: {(cm is null ? "null" : $"{cm.Connectors.Size} conn")}) → return []");
            return [];
        }

        SmartConLogger.Debug($"  connector[{connectorIndex}] found, Radius={connector.Radius:F6} ft ({connector.Radius * FeetToMm:F2} mm)");

        var mepInfo = connector.GetMEPConnectorInfo() as MEPFamilyConnectorInfo;
        if (mepInfo is null)
        {
            SmartConLogger.Debug("  GetMEPConnectorInfo()=null (not MEPFamilyConnectorInfo) → return []");
            return [];
        }

        SmartConLogger.Debug("  MEPFamilyConnectorInfo obtained");

        var radiusParamId = mepInfo.GetAssociateFamilyParameterId(new ElementId(BuiltInParameter.CONNECTOR_RADIUS));
        var diamParamId = mepInfo.GetAssociateFamilyParameterId(new ElementId(BuiltInParameter.CONNECTOR_DIAMETER));

        SmartConLogger.Debug($"  GetAssociateFamilyParameterId: CONNECTOR_RADIUS → id={radiusParamId.GetValue()}, CONNECTOR_DIAMETER → id={diamParamId.GetValue()}");

        bool useRadius = radiusParamId.GetValue() > 0;
        bool useDiameter = !useRadius && diamParamId.GetValue() > 0;

        if (!useRadius && !useDiameter)
        {
            SmartConLogger.Debug("  WARNING: no bound parameter to CONNECTOR_RADIUS/DIAMETER → return []");
            SmartConLogger.Warn($"[Resolver] elementId={elementId.GetValue()}: no CONNECTOR_RADIUS or CONNECTOR_DIAMETER binding");
            return [];
        }

        var activeParamId = useRadius ? radiusParamId : diamParamId;
        bool isDiameter = useDiameter;
        SmartConLogger.Debug($"  Using: {(useRadius ? "CONNECTOR_RADIUS" : "CONNECTOR_DIAMETER")}, activeParamId={activeParamId.GetValue()}, isDiameter={isDiameter}");

        var familyParamElem = doc.GetElement(activeParamId);
        var paramName = familyParamElem?.Name;
        SmartConLogger.Debug($"  ParameterElement.Name='{paramName}' (elementType={familyParamElem?.GetType().Name})");

        if (string.IsNullOrEmpty(paramName))
        {
            SmartConLogger.Debug("  WARNING: failed to get parameter name from ParameterElement → return []");
            SmartConLogger.Warn($"[Resolver] elementId={elementId.GetValue()}: parameter name is empty");
            return [];
        }

        var instParam = element.LookupParameter(paramName);
        bool isInstance = instParam is not null;
        bool isReadOnly = instParam?.IsReadOnly ?? false;

        SmartConLogger.Debug($"  LookupParameter('{paramName}'): isInstance={isInstance}, isReadOnly={isReadOnly}");

        if (isInstance && !isReadOnly)
        {
            SmartConLogger.Debug($"  → Direct instance parameter: '{paramName}', isDiameter={isDiameter}");
            var dep = new ParameterDependency(
                BuiltIn: null,
                SharedParamName: null,
                Formula: null,
                IsInstance: true,
                DirectParamName: paramName,
                RootParamName: null,
                IsDiameter: isDiameter);
            Cache(elementId, connectorIndex, dep);
            SmartConLogger.Debug($"  → dep: IsInstance=true, DirectParamName='{paramName}', IsDiameter={isDiameter}");
            return [dep];
        }

        if (isInstance && isReadOnly)
        {
            var dep = EditFamilySession.Run<ParameterDependency?>(doc, instance, familyDoc =>
            {
                var (directName, rootName, formula, _, _) =
                    FamilyParameterAnalyzer.AnalyzeConnectorRadiusParam(
                        familyDoc, instance.GetTransform(), connector.CoordinateSystem.Origin,
                        instance.HandFlipped, instance.FacingFlipped);

                SmartConLogger.Debug($"  FPA: directName='{directName}', rootName='{rootName}', formula='{formula}'");

                if (directName is not null && formula is not null && rootName is not null)
                {
                    return new ParameterDependency(
                        BuiltIn: null,
                        SharedParamName: null,
                        Formula: formula,
                        IsInstance: true,
                        DirectParamName: directName,
                        RootParamName: rootName,
                        IsDiameter: isDiameter);
                }

                SmartConLogger.Debug("  FPA did not return full chain (directName/formula/rootName null)");
                return null;
            });

            if (dep is not null)
            {
                Cache(elementId, connectorIndex, dep);
                SmartConLogger.Debug($"  → dep with formula: DirectParamName='{dep.DirectParamName}', RootParamName='{dep.RootParamName}', Formula='{dep.Formula}'");
                return [dep];
            }
        }

        {
            SmartConLogger.Debug($"  → Type / ReadOnly param without formula: ChangeTypeId will be used");
            SmartConLogger.Debug($"    paramName='{paramName}', isInstance={isInstance}, isReadOnly={isReadOnly}, isDiameter={isDiameter}");
            var dep = new ParameterDependency(
                BuiltIn: null,
                SharedParamName: null,
                Formula: null,
                IsInstance: false,
                DirectParamName: paramName,
                RootParamName: null,
                IsDiameter: isDiameter);
            Cache(elementId, connectorIndex, dep);
            SmartConLogger.Debug($"  → dep: IsInstance=false (ChangeTypeId), DirectParamName='{paramName}'");
            return [dep];
        }
    }

    public bool TrySetConnectorRadius(Document doc, ElementId elementId,
        int connectorIndex, double targetRadiusInternalUnits)
    {
        SmartConLogger.DebugSection("TrySetConnectorRadius");
        SmartConLogger.Debug($"  elementId={elementId.GetValue()}, connIdx={connectorIndex}, targetRadius={targetRadiusInternalUnits:F6} ft ({targetRadiusInternalUnits * FeetToMm:F2} mm)");

        var element = doc.GetElement(elementId);
        if (element is null)
        {
            SmartConLogger.Debug("  element=null → return false");
            return false;
        }

        if (element is MEPCurve or FlexPipe)
        {
            var diamParam = element.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
            if (diamParam is null || diamParam.IsReadOnly)
            {
                SmartConLogger.Debug("  MEPCurve: RBS_PIPE_DIAMETER_PARAM=null or ReadOnly → return false");
                return false;
            }
            double diamVal = targetRadiusInternalUnits * 2.0;
            SmartConLogger.Debug($"  MEPCurve: RBS_PIPE_DIAMETER_PARAM.Set({diamVal:F6} ft = {diamVal * FeetToMm:F2} mm)");
            diamParam.Set(diamVal);
            SmartConLogger.Debug("  → return true");
            return true;
        }

        if (element is not FamilyInstance)
        {
            SmartConLogger.Debug($"  not FamilyInstance → return false");
            return false;
        }

        _cache.TryGetValue((elementId.GetValue(), connectorIndex), out var dep);
        SmartConLogger.Debug($"  Cached dep: {(dep is null ? "null (no cache)" : $"IsInstance={dep.IsInstance}, Formula='{dep.Formula}', DirectParamName='{dep.DirectParamName}', RootParamName='{dep.RootParamName}', IsDiameter={dep.IsDiameter}")}");

        if (dep is not null)
        {
            double targetValue = dep.IsDiameter
                ? targetRadiusInternalUnits * 2.0
                : targetRadiusInternalUnits;
            SmartConLogger.Debug($"  targetValue={targetValue:F6} ft (IsDiameter={dep.IsDiameter}, radius={targetRadiusInternalUnits:F6})");

            if (dep.IsInstance && dep.Formula is null && dep.DirectParamName is not null)
            {
                var param = element.LookupParameter(dep.DirectParamName);
                SmartConLogger.Debug($"  LookupParameter('{dep.DirectParamName}'): {(param is null ? "null" : $"IsReadOnly={param.IsReadOnly}, StorageType={param.StorageType}")}");
                if (param is not null && !param.IsReadOnly)
                {
                    param.Set(targetValue);
                    SmartConLogger.Debug($"  → Direct write '{dep.DirectParamName}'={targetValue:F6} → return true");
                    return true;
                }
                SmartConLogger.Debug($"  → Direct write failed (null or ReadOnly)");
            }

            if (dep.IsInstance && dep.Formula is not null && dep.RootParamName is not null)
            {
                SmartConLogger.Debug($"  SolveFor('{dep.Formula}', '{dep.RootParamName}', {targetValue:F6})...");
                var value = FormulaSolver.SolveForStatic(
                    dep.Formula, dep.RootParamName, targetValue);
                SmartConLogger.Debug($"  SolveFor result: {(value is null ? "null (non-linear/unsupported)" : $"{value.Value:F6}")}");
                if (value is not null)
                {
                    var param = element.LookupParameter(dep.RootParamName);
                    SmartConLogger.Debug($"  LookupParameter('{dep.RootParamName}'): {(param is null ? "null" : $"IsReadOnly={param.IsReadOnly}")}");
                    if (param is not null && !param.IsReadOnly)
                    {
                        param.Set(value.Value);
                        SmartConLogger.Debug($"  → Write via formula '{dep.RootParamName}'={value.Value:F6} → return true");
                        return true;
                    }
                }
                SmartConLogger.Debug("  → SolveFor returned no result or ReadOnly → return false (dep.IsInstance=True, type unchanged)");
                return false;
            }

            if (dep.IsInstance)
            {
                SmartConLogger.Debug("  dep.IsInstance=True: fallback → TryChangeTypeTo...");
                return TryChangeTypeTo(doc, elementId, connectorIndex, targetRadiusInternalUnits);
            }
        }
        else
        {
            SmartConLogger.Debug("  dep=null in cache → falling through to TryChangeTypeTo");
        }

        SmartConLogger.Debug("  → TryChangeTypeTo (type param)...");
        return TryChangeTypeTo(doc, elementId, connectorIndex, targetRadiusInternalUnits);
    }

    private bool TryChangeTypeTo(Document doc, ElementId elementId,
        int connectorIndex, double targetRadius)
    {
        SmartConLogger.DebugSection("TryChangeTypeTo");
        SmartConLogger.Debug($"  elementId={elementId.GetValue()}, connIdx={connectorIndex}, targetRadius={targetRadius:F6} ft ({targetRadius * FeetToMm:F2} mm)");

        var element = doc.GetElement(elementId) as FamilyInstance;
        if (element is null)
        {
            SmartConLogger.Debug("  element=null → return false");
            return false;
        }

        var family = element.Symbol?.Family;
        if (family is null)
        {
            SmartConLogger.Debug("  family=null → return false");
            return false;
        }

        var symbolIds = family.GetFamilySymbolIds().ToList();
        SmartConLogger.Debug($"  Family '{family.Name}': {symbolIds.Count} symbols");

        var originalSymbolId = element.Symbol?.Id;
        ElementId? bestSymbolId = null;
        double bestDelta = double.MaxValue;
        string? bestSymbolName = null;

        foreach (var symbolId in symbolIds)
        {
            try
            {
                var inst = doc.GetElement(elementId) as FamilyInstance;
                if (inst is null) continue;

                var symbolElem = doc.GetElement(symbolId) as FamilySymbol;
                SmartConLogger.Debug($"  Trying symbolId={symbolId.GetValue()} ('{symbolElem?.Name}')...");

                inst.ChangeTypeId(symbolId);
                doc.Regenerate();

                var cm = inst.MEPModel?.ConnectorManager;
                var conn = cm?.FindByIndex(connectorIndex);
                if (conn is null)
                {
                    SmartConLogger.Debug($"    connector[{connectorIndex}]=null after type change");
                    continue;
                }

                double newRadius = conn.Radius;
                double delta = System.Math.Abs(newRadius - targetRadius);

                SmartConLogger.Debug($"    newRadius={newRadius:F6} ft ({newRadius * FeetToMm:F2} mm), delta={delta:F6}");

                if (delta < Epsilon)
                {
                    SmartConLogger.Debug($"    → EXACT match! symbolId={symbolId.GetValue()} ('{symbolElem?.Name}') → return true");
                    return true;
                }

                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    bestSymbolId = symbolId;
                    bestSymbolName = symbolElem?.Name;
                }
            }
            catch (Exception ex)
            {
                SmartConLogger.Debug($"  EXCEPTION for symbolId={symbolId.GetValue()}: {ex.GetType().Name}: {ex.Message}");
                SmartConLogger.Warn($"[Resolver] ChangeTypeId symbolId={symbolId.GetValue()} failed: {ex.Message}");
            }
        }

        if (bestSymbolId is not null)
        {
            SmartConLogger.Debug($"  → Nearest: symbolId={bestSymbolId.GetValue()} ('{bestSymbolName}'), delta={bestDelta:F6} ft ({bestDelta * FeetToMm:F2} mm)");

            if (bestSymbolId == originalSymbolId)
            {
                var inst = doc.GetElement(elementId) as FamilyInstance;
                inst?.ChangeTypeId(originalSymbolId);
                doc.Regenerate();
                SmartConLogger.Debug($"  → Nearest matches original type — restoring");
                return false;
            }

            try
            {
                var inst = doc.GetElement(elementId) as FamilyInstance;
                var prevSymbolName = inst?.Symbol?.Name;
                inst?.ChangeTypeId(bestSymbolId);
                doc.Regenerate();

                var afterInst = doc.GetElement(elementId) as FamilyInstance;
                SmartConLogger.Debug($"  → ChangeTypeId: '{prevSymbolName}' → '{bestSymbolName}'");

                if (afterInst is not null)
                {
                    var afterCm = afterInst.MEPModel?.ConnectorManager;
                    if (afterCm is not null)
                    {
                        SmartConLogger.Debug($"  → Post-type-change diagnostics (connIdx={connectorIndex}, target={targetRadius * FeetToMm:F2}mm):");
                        foreach (Connector ac in afterCm.Connectors)
                        {
                            if (ac.ConnectorType == ConnectorType.Curve) continue;
                            SmartConLogger.Debug($"     conn[{ac.Id}]: R={ac.Radius * FeetToMm:F2}mm, " +
                            $"domain={ac.Domain}, connected={(ac.AllRefs?.Size > 0)}");
                        }
                    }
                }

                SmartConLogger.Debug($"  → ChangeTypeId nearest done → return false (NeedsAdapter)");
            }
            catch (Exception ex)
            {
                SmartConLogger.Debug($"  EXCEPTION ChangeTypeId nearest: {ex.Message}");
                SmartConLogger.Warn($"[Resolver] ChangeTypeId nearest failed: {ex.Message}");
            }
            return false;
        }

        SmartConLogger.Debug("  → No suitable symbol found → return false");
        return false;
    }

    public (bool StaticExact, double AchievedDynRadius) TrySetFittingTypeForPair(
        Document doc, ElementId fittingId,
        int staticConnIdx, double staticRadius,
        int dynConnIdx, double dynRadius)
    {
        SmartConLogger.DebugSection("TrySetFittingTypeForPair");
        SmartConLogger.Debug($"  fittingId={fittingId.GetValue()}, staticConn={staticConnIdx} R={staticRadius:F6} ft ({staticRadius * FeetToMm:F2}mm), dynConn={dynConnIdx} R={dynRadius:F6} ft ({dynRadius * FeetToMm:F2}mm)");

        var element = doc.GetElement(fittingId) as FamilyInstance;
        if (element is null)
        {
            SmartConLogger.Debug("  element=null → return (false, dynRadius)");
            return (false, dynRadius);
        }

        var family = element.Symbol?.Family;
        if (family is null)
        {
            SmartConLogger.Debug("  family=null → return (false, dynRadius)");
            return (false, dynRadius);
        }

        var symbolIds = family.GetFamilySymbolIds().ToList();
        SmartConLogger.Debug($"  Family '{family.Name}': {symbolIds.Count} symbols");

        ElementId? bestExactId = null;
        double bestExactDynDelta = double.MaxValue;
        double bestExactDynRadius = dynRadius;

        ElementId? bestFallbackId = null;
        double bestFallbackStaticDelta = double.MaxValue;
        double bestFallbackDynRadius = dynRadius;

        foreach (var symbolId in symbolIds)
        {
            try
            {
                var inst = doc.GetElement(fittingId) as FamilyInstance;
                if (inst is null) continue;

                var sym = doc.GetElement(symbolId) as FamilySymbol;
                SmartConLogger.Debug($"  Trying symbolId={symbolId.GetValue()} ('{sym?.Name}')...");

                inst.ChangeTypeId(symbolId);
                doc.Regenerate();

                var cm = inst.MEPModel?.ConnectorManager;
                var staticConn = cm?.FindByIndex(staticConnIdx);
                var dynConn = cm?.FindByIndex(dynConnIdx);

                if (staticConn is null || dynConn is null)
                {
                    SmartConLogger.Debug($"    conn=null after type change");
                    continue;
                }

                double staticDelta = System.Math.Abs(staticConn.Radius - staticRadius);
                double dynDelta = System.Math.Abs(dynConn.Radius - dynRadius);
                SmartConLogger.Debug($"    staticR={staticConn.Radius:F6} (Δ={staticDelta:F6}), dynR={dynConn.Radius:F6} (Δ={dynDelta:F6})");

                if (staticDelta < Epsilon)
                {
                    if (dynDelta < bestExactDynDelta)
                    {
                        bestExactDynDelta = dynDelta;
                        bestExactId = symbolId;
                        bestExactDynRadius = dynConn.Radius;
                        SmartConLogger.Debug($"    → New best (static exact): dynΔ={dynDelta:F6}");
                    }
                }

                if (staticDelta < bestFallbackStaticDelta)
                {
                    bestFallbackStaticDelta = staticDelta;
                    bestFallbackId = symbolId;
                    bestFallbackDynRadius = dynConn.Radius;
                    SmartConLogger.Debug($"    → New best fallback: staticΔ={staticDelta:F6}");
                }
            }
            catch (Exception ex)
            {
                SmartConLogger.Debug($"  EXCEPTION for symbolId={symbolId.GetValue()}: {ex.Message}");
                SmartConLogger.Warn($"[TrySetFittingTypeForPair] symbolId={symbolId.GetValue()}: {ex.Message}");
            }
        }

        bool staticExact = bestExactId is not null;
        ElementId? winnerId = bestExactId ?? bestFallbackId;
        double achievedDynR = staticExact ? bestExactDynRadius : bestFallbackDynRadius;

        SmartConLogger.Debug($"  Winner: staticExact={staticExact}, symbolId={winnerId?.GetValue()}, achievedDynR={achievedDynR:F6} ft ({achievedDynR * FeetToMm:F2}mm)");

        if (winnerId is not null)
        {
            try
            {
                var inst = doc.GetElement(fittingId) as FamilyInstance;
                if (inst is not null)
                {
                    inst.ChangeTypeId(winnerId);
                    SmartConLogger.Debug($"  → ChangeTypeId({winnerId.GetValue()}) applied");
                }
            }
            catch (Exception ex)
            {
                SmartConLogger.Debug($"  EXCEPTION applying winner: {ex.Message}");
                SmartConLogger.Warn($"[TrySetFittingTypeForPair] ChangeTypeId winner failed: {ex.Message}");
            }
        }
        else
        {
            SmartConLogger.Debug("  → No suitable symbol found");
        }

        return (staticExact, achievedDynR);
    }

    private void Cache(ElementId elementId, int connectorIndex, ParameterDependency dep)
        => _cache[(elementId.GetValue(), connectorIndex)] = dep;
}
