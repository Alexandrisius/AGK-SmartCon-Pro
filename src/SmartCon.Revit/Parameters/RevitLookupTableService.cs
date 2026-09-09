using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Math;
using SmartCon.Core.Math.FormulaEngine.Solver;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Compatibility;
using SmartCon.Revit.Extensions;
using SmartCon.Core;
using SmartCon.Core.Compatibility;


using static SmartCon.Core.Units;
namespace SmartCon.Revit.Parameters;

public sealed partial class RevitLookupTableService(FamilyFormulaCache formulaCache) : ILookupTableService
{
    public bool ConnectorRadiusExistsInTable(Document doc, ElementId elementId,
        int connectorIndex, double radiusInternalUnits,
        IReadOnlyList<LookupColumnConstraint>? constraints = null)
    {
        using var _scope = SmartConLogger.BeginScope("LookupSvc",
            ("Method", "ConnectorRadiusExistsInTable"),
            ("ElementId", elementId.GetValue()),
            ("ConnectorIndex", connectorIndex));

        SmartConLogger.DebugSection("ConnectorRadiusExistsInTable");
        SmartConLogger.Debug($"  elementId={elementId.GetValue()}, connIdx={connectorIndex}, radiusInternal={radiusInternalUnits:F6} ft ({radiusInternalUnits * FeetToMm:F2} mm)");

        var ctx = BuildLookupContext(doc, elementId, connectorIndex);
        if (ctx is null)
        {
            SmartConLogger.Debug("  → ctx=null, no table → return false");
            return false;
        }

        var values = LookupTableCsvParser.ExtractColumnValues(ctx.CsvLines, ctx.ColIndex, ctx.AllQueryColumns, constraints);
        var distinct = values.Distinct().OrderBy(v => v).ToList();
        double targetMm = ToMillimeters(radiusInternalUnits, ctx.IsRadius);

        SmartConLogger.Debug($"  targetMm={targetMm:F3} (isRadius={ctx.IsRadius}), column values: {distinct.Count}");
        SmartConLogger.Debug($"  Table values (mm): [{string.Join(", ", distinct.Select(v => $"{v:F2}"))}]");

        bool found = distinct.Any(v => System.Math.Abs(v - targetMm) < Tolerance.LookupRadiusMatchMm);
        SmartConLogger.Debug($"  → ExistsInTable={found} (tolerance {Tolerance.LookupRadiusMatchMm} mm)");
        return found;
    }

    public double GetNearestAvailableRadius(Document doc, ElementId elementId,
        int connectorIndex, double targetRadiusInternalUnits,
        IReadOnlyList<LookupColumnConstraint>? constraints = null)
    {
        using var _scope = SmartConLogger.BeginScope("LookupSvc",
            ("Method", "GetNearestAvailableRadius"),
            ("ElementId", elementId.GetValue()),
            ("ConnectorIndex", connectorIndex));

        SmartConLogger.DebugSection("GetNearestAvailableRadius");
        SmartConLogger.Debug($"  elementId={elementId.GetValue()}, connIdx={connectorIndex}, targetInternal={targetRadiusInternalUnits:F6} ft");

        var ctx = BuildLookupContext(doc, elementId, connectorIndex);
        if (ctx is null)
        {
            SmartConLogger.Debug("  → ctx=null, no table → return target as-is");
            return targetRadiusInternalUnits;
        }

        var values = LookupTableCsvParser.ExtractColumnValues(ctx.CsvLines, ctx.ColIndex, ctx.AllQueryColumns, constraints);
        if (values.Count == 0)
        {
            SmartConLogger.Debug("  → column empty → return target as-is");
            return targetRadiusInternalUnits;
        }

        var distinct = values.Distinct().OrderBy(v => v).ToList();
        double targetMm = ToMillimeters(targetRadiusInternalUnits, ctx.IsRadius);
        SmartConLogger.Debug($"  targetMm={targetMm:F3}, column values: {distinct.Count}");
        SmartConLogger.Debug($"  Values (mm): [{string.Join(", ", distinct.Select(v => $"{v:F2}"))}]");

        double nearestMm = distinct[0];
        double nearestDiff = System.Math.Abs(distinct[0] - targetMm);

        foreach (var v in distinct.Skip(1))
        {
            var diff = System.Math.Abs(v - targetMm);
            if (diff < nearestDiff)
            {
                nearestDiff = diff;
                nearestMm = v;
            }
        }

        double result = FromMillimeters(nearestMm, ctx.IsRadius);
        SmartConLogger.Debug($"  → nearest={nearestMm:F2} mm (delta={nearestDiff:F3} mm), internal={result:F6} ft");
        return result;
    }

    public bool HasLookupTable(Document doc, ElementId elementId, int connectorIndex)
    {
        using var _scope = SmartConLogger.BeginScope("LookupSvc",
            ("Method", "HasLookupTable"),
            ("ElementId", elementId.GetValue()),
            ("ConnectorIndex", connectorIndex));

        SmartConLogger.DebugSection("HasLookupTable");
        SmartConLogger.Debug($"  elementId={elementId.GetValue()}, connIdx={connectorIndex}");
        bool has = BuildLookupContext(doc, elementId, connectorIndex) is not null;
        SmartConLogger.Debug($"  → HasLookupTable={has}");
        return has;
    }

    public AllSizeRowsResult GetAllSizeRows(Document doc, ElementId elementId,
        int targetConnectorIndex,
        IReadOnlyList<LookupColumnConstraint>? constraints = null)
    {
        SmartConLogger.DebugSection("GetAllSizeRows");
        SmartConLogger.Debug($"  elementId={elementId.GetValue()}, targetConnIdx={targetConnectorIndex}, constraints={constraints?.Count ?? 0}");

        var element = doc.GetElement(elementId);
        if (element is null or MEPCurve or FlexPipe)
        {
            SmartConLogger.Debug("  element=null or MEPCurve → return []");
            return new AllSizeRowsResult([], new HashSet<string>(), [], new HashSet<long>());
        }

        if (element is not FamilyInstance instance)
        {
            SmartConLogger.Debug($"  not FamilyInstance → return []");
            return new AllSizeRowsResult([], new HashSet<string>(), [], new HashSet<long>());
        }

        var cm = instance.MEPModel?.ConnectorManager;
        if (cm is null)
        {
            SmartConLogger.Debug("  ConnectorManager=null → return []");
            return new AllSizeRowsResult([], new HashSet<string>(), [], new HashSet<long>());
        }

        var currentRadii = new Dictionary<int, double>();
        foreach (Connector c in cm.Connectors)
        {
            if (c.ConnectorType == ConnectorType.Curve) continue;
            if (!c.IsRoundSafe()) continue;
            currentRadii[(int)c.Id] = c.Radius;
        }

        var allConnectorIndices = new List<int>();
        foreach (Connector c in cm.Connectors)
        {
            if (c.ConnectorType == ConnectorType.Curve) continue;
            if (!c.IsRoundSafe()) continue;
            allConnectorIndices.Add((int)c.Id);
        }
        SmartConLogger.Debug($"  allConnectorIndices: [{string.Join(", ", allConnectorIndices)}]");

        var family = instance.Symbol?.Family;
        if (family is null)
        {
            SmartConLogger.Debug("  family=null → return []");
            return new AllSizeRowsResult([], new HashSet<string>(), [], new HashSet<long>());
        }

        // Tables read directly from the PROJECT document (FamilySizeTableManager
        // works there); formulas come from the cached snapshot — one EditFamily
        // per family per session instead of one per call (phase 3, #161).
        var fstm = FamilySizeTableManager.GetFamilySizeTableManager(doc, family.Id);
        if (fstm is null || fstm.NumberOfSizeTables == 0)
        {
            SmartConLogger.Debug("  no size tables → return []");
            return new AllSizeRowsResult([], new HashSet<string>(), [], new HashSet<long>());
        }

        var snapshot = formulaCache.Get(doc, instance);
        if (snapshot is null)
        {
            SmartConLogger.Debug("  formula snapshot unavailable (EditFamily forbidden) → return []");
            return new AllSizeRowsResult([], new HashSet<string>(), [], new HashSet<long>());
        }
        var paramSnapshot = snapshot.Parameters;
        var formulaByName = snapshot.FormulaByName;

        var connectorParamMap = new Dictionary<int, string>();
        foreach (var connIdx in allConnectorIndices)
        {
            var connector = cm.FindByIndex(connIdx);
            if (connector is null) continue;
            var binding = ConnectorSizeBindingResolver.TryGetSizeBinding(doc, connector);
            if (binding is null)
            {
                SmartConLogger.Debug($"    conn[{connIdx}]: no size binding (not found)");
                continue;
            }
            var (directName, rootName, formula, _, isDiameter) =
                FamilyParameterAnalyzer.AnalyzeConnectorRadiusParam(
                    snapshot, binding.Value.ParamName, binding.Value.IsDiameter);
            var searchParam = rootName ?? directName;
            if (searchParam is not null)
            {
                connectorParamMap[connIdx] = searchParam;
                SmartConLogger.Debug($"    conn[{connIdx}]: searchParam='{searchParam}', directName='{directName}', rootName='{rootName}'");
            }
            else
            {
                SmartConLogger.Debug($"    conn[{connIdx}]: searchParam=NULL (not found)");
            }
        }
        SmartConLogger.Debug($"  connectorParamMap: [{string.Join(", ", connectorParamMap.Select(kvp => $"conn[{kvp.Key}]='{kvp.Value}'"))}]");

        var tableNames = fstm.GetAllSizeTableNames().ToList();

        var perTableRows = new List<List<SizeTableRow>>();
        foreach (var tableName in tableNames)
        {
            var tableRows = ExtractRowsFromTable(
                fstm, tableName, paramSnapshot, formulaByName,
                targetConnectorIndex, connectorParamMap,
                allConnectorIndices, currentRadii, constraints);
            if (tableRows.Count > 0)
                perTableRows.Add(tableRows);
        }

        var allNonSizeParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tableRows in perTableRows)
            foreach (var row in tableRows)
                foreach (var key in row.NonSizeParameterValues.Keys)
                    allNonSizeParams.Add(key);

        if (allNonSizeParams.Count > 0)
            SmartConLogger.Debug($"  allNonSizeParams (union from {perTableRows.Count} tables): [{string.Join(", ", allNonSizeParams)}]");

        List<SizeTableRow> allRows;
        var validDn = new HashSet<long>();

        if (perTableRows.Count <= 1)
        {
            allRows = perTableRows.Count == 1 ? perTableRows[0] : [];
        }
        else
        {
            var dnSets = perTableRows.Select(rows =>
                new HashSet<long>(rows.Select(r => RoundDnToMicrons(r.TargetRadiusFt)))).ToList();

            validDn = new HashSet<long>(dnSets[0]);
            for (int i = 1; i < dnSets.Count; i++)
                validDn.IntersectWith(dnSets[i]);

            SmartConLogger.Debug($"  {perTableRows.Count} tables, DN: [{string.Join(" ∩ ", dnSets.Select(s => s.Count))}] → {validDn.Count}");

            var bestTable = perTableRows
                .OrderByDescending(t => t.Max(r => r.ConnectorRadiiFt.Count))
                .ThenByDescending(t => t.Count)
                .First();
            allRows = bestTable
                .Where(r => validDn.Contains(RoundDnToMicrons(r.TargetRadiusFt)))
                .ToList();
        }

        var distinct = DeduplicateRows(allRows);

        int maxConnCount = distinct.Count > 0 ? distinct.Max(r => r.ConnectorRadiiFt.Count) : 0;
        if (maxConnCount > 0)
            distinct = distinct.Where(r => r.ConnectorRadiiFt.Count == maxConnCount).ToList();

        SmartConLogger.Debug($"  → {distinct.Count} unique configs");

        var readOnlyPerTableRows = perTableRows
            .Select(t => (IReadOnlyList<SizeTableRow>)t.AsReadOnly())
            .ToList()
            .AsReadOnly();

        return new AllSizeRowsResult(
            distinct.AsReadOnly(),
            allNonSizeParams,
            readOnlyPerTableRows,
            validDn);
    }

    internal static long RoundDnToMicrons(double radiusFt)
        => (long)System.Math.Round(radiusFt * FeetToMm * 2.0 * 1000.0);

    private static double ToMillimeters(double internalUnits, bool isRadius)
    {
        double mm = internalUnits * FeetToMm;
        return isRadius ? mm : mm * 2.0;
    }

    private static double FromMillimeters(double mm, bool isRadius)
    {
        double feet = mm * MmToFeet;
        return isRadius ? feet : feet / 2.0;
    }
}

