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
using SmartCon.Revit.Extensions;
using RevitTransform = Autodesk.Revit.DB.Transform;
using SmartCon.Core;
using SmartCon.Core.Compatibility;


using static SmartCon.Core.Units;
namespace SmartCon.Revit.Parameters;

public sealed class RevitLookupTableService : ILookupTableService
{
    public bool ConnectorRadiusExistsInTable(Document doc, ElementId elementId,
        int connectorIndex, double radiusInternalUnits,
        IReadOnlyList<LookupColumnConstraint>? constraints = null)
    {
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
        SmartConLogger.DebugSection("HasLookupTable");
        SmartConLogger.Debug($"  elementId={elementId.GetValue()}, connIdx={connectorIndex}");
        bool has = BuildLookupContext(doc, elementId, connectorIndex) is not null;
        SmartConLogger.Debug($"  → HasLookupTable={has}");
        return has;
    }

    public IReadOnlyList<SizeTableRow> GetAllSizeRows(Document doc, ElementId elementId,
        int targetConnectorIndex,
        IReadOnlyList<LookupColumnConstraint>? constraints = null)
    {
        SmartConLogger.DebugSection("GetAllSizeRows");
        SmartConLogger.Debug($"  elementId={elementId.GetValue()}, targetConnIdx={targetConnectorIndex}, constraints={constraints?.Count ?? 0}");

        var element = doc.GetElement(elementId);
        if (element is null or MEPCurve or FlexPipe)
        {
            SmartConLogger.Debug("  element=null or MEPCurve → return []");
            return [];
        }

        if (element is not FamilyInstance instance)
        {
            SmartConLogger.Debug($"  not FamilyInstance → return []");
            return [];
        }

        var cm = instance.MEPModel?.ConnectorManager;
        if (cm is null)
        {
            SmartConLogger.Debug("  ConnectorManager=null → return []");
            return [];
        }

        var instanceTransform = instance.GetTransform();

        var currentRadii = new Dictionary<int, double>();
        foreach (Connector c in cm.Connectors)
        {
            if (c.ConnectorType == ConnectorType.Curve) continue;
            currentRadii[(int)c.Id] = c.Radius;
        }

        var allConnectorIndices = new List<int>();
        foreach (Connector c in cm.Connectors)
        {
            if (c.ConnectorType == ConnectorType.Curve) continue;
            allConnectorIndices.Add((int)c.Id);
        }
        SmartConLogger.Debug($"  allConnectorIndices: [{string.Join(", ", allConnectorIndices)}]");

        return EditFamilySession.Run<List<SizeTableRow>>(doc, instance, familyDoc =>
        {
            var fstm = FamilySizeTableManager.GetFamilySizeTableManager(familyDoc, familyDoc.OwnerFamily.Id);
            if (fstm is null || fstm.NumberOfSizeTables == 0)
            {
                SmartConLogger.Debug("  no size tables → return []");
                return [];
            }

            var snapshot = FamilyParameterSnapshot.Build(familyDoc.FamilyManager);
            var paramSnapshot = snapshot.Parameters;
            var formulaByName = snapshot.FormulaByName;

            var connectorParamMap = new Dictionary<int, string>();
            foreach (var connIdx in allConnectorIndices)
            {
                var connector = cm.FindByIndex(connIdx);
                if (connector is null) continue;
                var targetOriginGlobal = connector.CoordinateSystem.Origin;
                var (directName, rootName, formula, _, isDiameter) =
                    FamilyParameterAnalyzer.AnalyzeConnectorRadiusParam(
                        familyDoc, instanceTransform, targetOriginGlobal,
                        instance.HandFlipped, instance.FacingFlipped);
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

            List<SizeTableRow> allRows;
            if (perTableRows.Count <= 1)
            {
                allRows = perTableRows.Count == 1 ? perTableRows[0] : [];
            }
            else
            {
                var dnSets = perTableRows.Select(rows =>
                    new HashSet<long>(rows.Select(r => RoundDnToMicrons(r.TargetRadiusFt)))).ToList();

                var validDn = new HashSet<long>(dnSets[0]);
                for (int i = 1; i < dnSets.Count; i++)
                    validDn.IntersectWith(dnSets[i]);

                SmartConLogger.Debug($"  [Intersect] {perTableRows.Count} tables, DN: [{string.Join(" ∩ ", dnSets.Select(s => s.Count))}] → {validDn.Count}");

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
            return distinct;
        }) ?? [];
    }

    private sealed record TableColumnInfo(
        int CsvColIndex,
        string ParameterName,
        List<int> ConnectorIndices,
        bool StoresDiameters);

    private List<SizeTableRow> ExtractRowsFromTable(
        FamilySizeTableManager fstm,
        string tableName,
        IReadOnlyList<(string? Name, string? Formula)> paramSnapshot,
        IReadOnlyDictionary<string, string> formulaByName,
        int targetConnectorIndex,
        Dictionary<int, string> connectorParamMap,
        List<int> allConnectorIndices,
        Dictionary<int, double> currentRadii,
        IReadOnlyList<LookupColumnConstraint>? constraints)
    {
        var result = new List<SizeTableRow>();

        var queryParams = LookupColumnResolver.FindQueryParamsForTable(tableName, paramSnapshot, formulaByName);
        if (queryParams.Count == 0)
        {
            SmartConLogger.Debug($"    table '{tableName}': no query params → skip");
            return result;
        }

        var targetParam = connectorParamMap.GetValueOrDefault(targetConnectorIndex);
        if (targetParam is null)
        {
            SmartConLogger.Debug($"    table '{tableName}': no param for target conn[{targetConnectorIndex}] → skip");
            return result;
        }

        int targetColIndex = -1;
        var columns = new List<TableColumnInfo>();
        for (int i = 0; i < queryParams.Count; i++)
        {
            var qParam = queryParams[i];

            var matchingConnectors = connectorParamMap
                .Where(kvp => string.Equals(kvp.Value, qParam, StringComparison.OrdinalIgnoreCase))
                .Select(kvp => kvp.Key)
                .ToList();

            if (matchingConnectors.Count == 0)
            {
                var qDigits = LookupColumnResolver.ExtractTrailingDigits(qParam);
                if (qDigits is not null)
                {
                    matchingConnectors = connectorParamMap
                        .Where(kvp =>
                        {
                            var cDigits = LookupColumnResolver.ExtractTrailingDigits(kvp.Value);
                            return cDigits == qDigits;
                        })
                        .Select(kvp => kvp.Key)
                        .ToList();
                }
            }

            if (matchingConnectors.Count == 0)
            {
                matchingConnectors = connectorParamMap
                    .Where(kvp => LookupColumnResolver.DependsOn(formulaByName, kvp.Value, qParam)
                               || LookupColumnResolver.DependsOn(formulaByName, qParam, kvp.Value))
                    .Select(kvp => kvp.Key)
                    .ToList();
                SmartConLogger.Debug($"    col[{i}] qp='{qParam}': strict=0, suffix=0, DependsOn=[{string.Join(",", matchingConnectors)}], connMap=[{string.Join(",", connectorParamMap.Select(kvp => $"{kvp.Key}:{kvp.Value}"))}]");
            }
            else
            {
                SmartConLogger.Debug($"    col[{i}] qp='{qParam}': matched=[{string.Join(",", matchingConnectors)}]");
            }

            bool isTarget = string.Equals(qParam, targetParam, StringComparison.OrdinalIgnoreCase);
            if (isTarget)
                targetColIndex = i + 1;

            columns.Add(new TableColumnInfo(
                CsvColIndex: i + 1,
                ParameterName: qParam,
                ConnectorIndices: matchingConnectors,
                StoresDiameters: true));
        }

        if (targetColIndex < 0)
        {
            for (int i = 0; i < queryParams.Count; i++)
            {
                if (LookupColumnResolver.DependsOn(formulaByName, queryParams[i], targetParam))
                {
                    targetColIndex = i + 1;
                    break;
                }
            }
        }

        if (targetColIndex < 0)
        {
            var tDigits = LookupColumnResolver.ExtractTrailingDigits(targetParam);
            if (tDigits is not null)
            {
                for (int i = 0; i < queryParams.Count; i++)
                {
                    var qDigits = LookupColumnResolver.ExtractTrailingDigits(queryParams[i]);
                    if (qDigits == tDigits)
                    {
                        targetColIndex = i + 1;
                        SmartConLogger.Debug($"    targetCol suffix-fallback: '{targetParam}' (suffix={tDigits}) ≈ '{queryParams[i]}' (suffix={qDigits}) @ colIndex={targetColIndex}");
                        break;
                    }
                }
            }
        }

        if (targetColIndex < 0)
        {
            SmartConLogger.Debug($"    table '{tableName}': target param '{targetParam}' not found in query columns → skip");
            return result;
        }

        SmartConLogger.Debug($"    table '{tableName}': targetCol={targetColIndex}, columns={columns.Count}");

        var sizeColumnIndices = new List<int>();
        int remappedTargetColIndex = -1;
        var assignedConnectors = new HashSet<int>();
        for (int i = 0; i < columns.Count; i++)
        {
            if (columns[i].ConnectorIndices.Count == 0) continue;

            var before = columns[i].ConnectorIndices.ToList();
            columns[i].ConnectorIndices.RemoveAll(ci => assignedConnectors.Contains(ci));
            if (columns[i].ConnectorIndices.Count == 0)
            {
                SmartConLogger.Debug($"    assignedCol[{i}] '{columns[i].ParameterName}': before=[{string.Join(",", before)}] → all already assigned (skip)");
                continue;
            }

            foreach (var ci in columns[i].ConnectorIndices)
                assignedConnectors.Add(ci);

            SmartConLogger.Debug($"    assignedCol[{i}] '{columns[i].ParameterName}': before=[{string.Join(",", before)}] → after=[{string.Join(",", columns[i].ConnectorIndices)}], assignedSoFar=[{string.Join(",", assignedConnectors)}]");

            if (columns[i].CsvColIndex == targetColIndex)
                remappedTargetColIndex = sizeColumnIndices.Count + 1;
            sizeColumnIndices.Add(i);
        }

        var unassignedCols = columns
            .Select((col, idx) => (col, idx))
            .Where(x => x.col.ConnectorIndices.Count == 0)
            .ToList();
        var freeConnectors = connectorParamMap.Keys
            .Except(assignedConnectors)
            .OrderBy(k => k)
            .ToList();

        if (unassignedCols.Count > 0 && freeConnectors.Count > 0)
        {
            SmartConLogger.Debug($"    fallback: {unassignedCols.Count} unassigned cols, {freeConnectors.Count} free connectors [{string.Join(",", freeConnectors)}]");

            foreach (var (col, idx) in unassignedCols.ToList())
            {
                if (freeConnectors.Count == 0) break;
                var qDigits = LookupColumnResolver.ExtractTrailingDigits(col.ParameterName);
                if (qDigits is null) continue;

                int? matchedConn = null;
                foreach (var fc in freeConnectors)
                {
                    var cParam = connectorParamMap[fc];
                    var cDigits = LookupColumnResolver.ExtractTrailingDigits(cParam);
                    if (cDigits == qDigits)
                    {
                        matchedConn = fc;
                        break;
                    }
                }

                if (matchedConn.HasValue)
                {
                    freeConnectors.Remove(matchedConn.Value);
                    col.ConnectorIndices.Add(matchedConn.Value);
                    assignedConnectors.Add(matchedConn.Value);

                    if (col.CsvColIndex == targetColIndex)
                        remappedTargetColIndex = sizeColumnIndices.Count + 1;
                    sizeColumnIndices.Add(idx);

                    var cParamName = connectorParamMap[matchedConn.Value];
                    SmartConLogger.Debug($"    suffix-fallback: col[{idx}] '{col.ParameterName}' (suffix={qDigits}) → conn[{matchedConn.Value}] (param={cParamName}, suffix={cParamName})");
                }
            }

            foreach (var (col, idx) in unassignedCols.Where(x => x.col.ConnectorIndices.Count == 0))
            {
                SmartConLogger.Debug($"    SKIP col[{idx}] '{col.ParameterName}' — not bound to connector (non-dimensional)");
            }
        }

        if (remappedTargetColIndex < 0 && sizeColumnIndices.Count > 0)
            remappedTargetColIndex = 1;

        var connectorGroups = sizeColumnIndices
            .Select(idx => (IReadOnlyList<int>)columns[idx].ConnectorIndices.AsReadOnly())
            .ToList();

        int effectiveUniqueParamCount = sizeColumnIndices.Count;
        if (effectiveUniqueParamCount == 0) effectiveUniqueParamCount = 1;

        SmartConLogger.Debug($"    sizeColumns={sizeColumnIndices.Count}/{columns.Count}, remappedTarget={remappedTargetColIndex}");

        var tempPath = Path.GetTempFileName();
        try
        {
            fstm.ExportSizeTable(tableName, tempPath);
            var lines = File.ReadAllLines(tempPath);

            for (int row = 1; row < lines.Length; row++)
            {
                var line = lines[row];
                if (string.IsNullOrWhiteSpace(line)) continue;

                var cols = line.Split(',');
                if (targetColIndex >= cols.Length) continue;

                if (constraints is { Count: > 0 } && columns.Count > 1)
                {
                    var csvMappings = columns.Select(c => new CsvColumnMapping(c.CsvColIndex, c.ParameterName)).ToList();
                    int filteredOut = 0;
                    bool rowOk = LookupTableCsvParser.ApplyConstraintFilter(cols, targetColIndex, csvMappings, constraints, row, ref filteredOut);
                    if (!rowOk) continue;
                }

                var cell = cols[targetColIndex].Trim().Trim('"');
                if (!LookupTableCsvParser.TryParseRevitValue(cell, out double targetVal)) continue;

                double targetRadiusFt = targetVal / 2.0 * MmToFeet;

                var connectorRadii = new Dictionary<int, double>();
                var queryParamRadii = new List<double>();
                foreach (var colIdx in sizeColumnIndices)
                {
                    var col = columns[colIdx];
                    if (col.CsvColIndex >= cols.Length) continue;
                    var c = cols[col.CsvColIndex].Trim().Trim('"');
                    if (!LookupTableCsvParser.TryParseRevitValue(c, out double val)) continue;
                    double rFt = val / 2.0 * MmToFeet;
                    queryParamRadii.Add(rFt);
                    foreach (var ci in col.ConnectorIndices)
                        connectorRadii[ci] = rFt;
                }

                if (connectorRadii.Count == 0)
                    connectorRadii[targetConnectorIndex] = targetRadiusFt;

                var sizeQueryParamNames = new List<string>();
                var sizeQueryParamValuesMm = new List<double>();
                foreach (var colIdx in sizeColumnIndices)
                {
                    var col = columns[colIdx];
                    if (col.CsvColIndex >= cols.Length) continue;
                    var cv = cols[col.CsvColIndex].Trim().Trim('"');
                    if (!LookupTableCsvParser.TryParseRevitValue(cv, out double qval)) continue;
                    sizeQueryParamNames.Add(col.ParameterName);
                    sizeQueryParamValuesMm.Add(qval);
                }

                result.Add(new SizeTableRow
                {
                    TargetColumnIndex = remappedTargetColIndex > 0 ? remappedTargetColIndex : 1,
                    TargetRadiusFt = targetRadiusFt,
                    ConnectorRadiiFt = connectorRadii,
                    QueryParameterRadiiFt = queryParamRadii,
                    UniqueQueryParameterCount = effectiveUniqueParamCount,
                    QueryParamConnectorGroups = connectorGroups,
                    QueryParamNames = sizeQueryParamNames,
                    QueryParamRawValuesMm = sizeQueryParamValuesMm
                });
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"    ExportSizeTable error: {ex.Message}");
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* temp file cleanup */ }
        }

        return result;
    }

    private static List<SizeTableRow> DeduplicateRows(List<SizeTableRow> rows)
    {
        var seen = new HashSet<string>();
        var result = new List<SizeTableRow>();
        foreach (var row in rows)
        {
            var key = string.Join("|", row.ConnectorRadiiFt
                .OrderBy(kvp => kvp.Key)
                .Select(kvp => $"{kvp.Key}:{kvp.Value:F8}"));
            if (seen.Add(key))
                result.Add(row);
        }
        return result;
    }

    private sealed record LookupContext(
        string[] CsvLines,
        int ColIndex,
        bool IsRadius,
        IReadOnlyList<CsvColumnMapping> AllQueryColumns);

    private LookupContext? BuildLookupContext(Document doc, ElementId elementId, int connectorIndex)
    {
        SmartConLogger.DebugSection("BuildLookupContext");
        var element = doc.GetElement(elementId);
        if (element is null)
        {
            SmartConLogger.Debug("  element=null → return null");
            return null;
        }

        SmartConLogger.Debug($"  element={element.Name} ({element.GetType().Name}), id={elementId.GetValue()}");

        if (element is MEPCurve or FlexPipe)
        {
            SmartConLogger.Debug("  element is MEPCurve/FlexPipe → no LookupTable → return null");
            return null;
        }

        if (element is not FamilyInstance instance)
        {
            SmartConLogger.Debug($"  element is not FamilyInstance ({element.GetType().Name}) → return null");
            return null;
        }

        var connector = (instance.MEPModel?.ConnectorManager)?.FindByIndex(connectorIndex);
        if (connector is null)
        {
            SmartConLogger.Debug($"  connector[{connectorIndex}]=null → return null");
            return null;
        }

        var targetOriginGlobal = connector.CoordinateSystem.Origin;
        var instanceTransform = instance.GetTransform();
        SmartConLogger.Debug($"  connector[{connectorIndex}] origin=({targetOriginGlobal.X:F4}, {targetOriginGlobal.Y:F4}, {targetOriginGlobal.Z:F4})");

        return EditFamilySession.Run<LookupContext?>(doc, instance, familyDoc =>
            BuildLookupContextFromFamily(familyDoc, instanceTransform, targetOriginGlobal,
                instance.HandFlipped, instance.FacingFlipped));
    }

    private static LookupContext? BuildLookupContextFromFamily(
        Document familyDoc,
        RevitTransform instanceTransform,
        XYZ targetOriginGlobal,
        bool handFlipped = false,
        bool facingFlipped = false)
    {
        SmartConLogger.DebugSection("BuildLookupContextFromFamily");

        var fstm = FamilySizeTableManager.GetFamilySizeTableManager(
            familyDoc, familyDoc.OwnerFamily.Id);

        if (fstm is null)
        {
            SmartConLogger.Debug("  FamilySizeTableManager=null → no tables in family");
            return null;
        }

        SmartConLogger.Debug($"  FamilySizeTableManager: NumberOfSizeTables={fstm.NumberOfSizeTables}");

        if (fstm.NumberOfSizeTables == 0)
        {
            SmartConLogger.Debug("  NumberOfSizeTables=0 → no tables → return null");
            return null;
        }

        var tableNames = fstm.GetAllSizeTableNames().ToList();
        SmartConLogger.Debug($"  Tables ({tableNames.Count}): [{string.Join(", ", tableNames)}]");

        var snapshot = FamilyParameterSnapshot.Build(familyDoc.FamilyManager);
        var paramSnapshot = snapshot.Parameters;
        var formulaByName = snapshot.FormulaByName;
        SmartConLogger.Debug($"  Pre-cached formulaByName: {formulaByName.Count} entries from {paramSnapshot.Count} parameters");

        SmartConLogger.Debug("  → FamilyParameterAnalyzer.AnalyzeConnectorRadiusParam...");
        var (directName, rootName, formula, isInstance, isDiameter) =
            FamilyParameterAnalyzer.AnalyzeConnectorRadiusParam(
                familyDoc, instanceTransform, targetOriginGlobal,
                handFlipped, facingFlipped);

        SmartConLogger.Debug($"  FPA result: directName='{directName}', rootName='{rootName}', formula='{formula}', isInstance={isInstance}, isDiameter={isDiameter}");

        var searchParamName = rootName ?? directName;
        if (searchParamName is null)
        {
            SmartConLogger.Debug("  searchParamName=null (FPA did not find parameter) → return null");
            return null;
        }

        SmartConLogger.Debug($"  searchParamName='{searchParamName}' (using for table lookup)");

        bool tableStoresDiameters;
        if (rootName is not null && formula is not null)
        {
            double refRadius = 1.0;
            double directRef = isDiameter ? refRadius * 2.0 : refRadius;
            var rootRef = FormulaSolver.SolveForStatic(formula, rootName, directRef);
            if (rootRef.HasValue)
            {
                tableStoresDiameters = System.Math.Abs(rootRef.Value / refRadius - 2.0) < 0.1;
                SmartConLogger.Debug($"  tableStoresDiameters={tableStoresDiameters} (SolveFor rootRef={rootRef.Value:F3}, ratio={rootRef.Value / refRadius:F3})");
            }
            else
            {
                bool isQueryParam = false;
                try
                {
                    var sl = FormulaSolver.ParseSizeLookupStatic(formula);
                    isQueryParam = sl is not null && sl.Value.QueryParameters
                        .Any(q => string.Equals(q, rootName, StringComparison.OrdinalIgnoreCase));
                }
                catch (Exception ex) { SmartConLogger.Warn($"[LookupSvc] size_lookup formula parse failed: {ex.GetType().Name}: {ex.Message}"); }

                tableStoresDiameters = isQueryParam || isDiameter;
                SmartConLogger.Debug($"  tableStoresDiameters={tableStoresDiameters} (SolveFor=null, isQueryParam={isQueryParam}, isDiameter={isDiameter})");
            }
        }
        else
        {
            tableStoresDiameters = isDiameter;
            SmartConLogger.Debug($"  tableStoresDiameters={tableStoresDiameters} (isDiameter from FPA, isRadius=!{tableStoresDiameters})");
        }

        foreach (var tableName in tableNames)
        {
            SmartConLogger.Debug($"  → TryGetContextForTable('{tableName}', searchParam='{searchParamName}')...");
            var ctx = TryGetContextForTable(fstm, tableName, searchParamName, tableStoresDiameters, paramSnapshot, formulaByName);
            if (ctx is not null)
            {
                SmartConLogger.Debug($"  ✓ Context found: tableName='{tableName}', colIndex={ctx.ColIndex}, isRadius={ctx.IsRadius}, CSV lines={ctx.CsvLines.Length}");
                return ctx;
            }
        }

        SmartConLogger.Debug($"  ✗ No table contains parameter '{searchParamName}' as queryParam → return null");
        return null;
    }

    private static LookupContext? TryGetContextForTable(
        FamilySizeTableManager fstm,
        string tableName,
        string searchParamName,
        bool tableStoresDiameters,
        IReadOnlyList<(string? Name, string? Formula)> paramSnapshot,
        IReadOnlyDictionary<string, string> formulaByName)
    {
        int targetColIndex = -1;
        string? foundInParam = null;
        bool foundViaDependsOn = false;
        IReadOnlyList<CsvColumnMapping>? allQueryColumns = null;

        SmartConLogger.Debug($"    Iterating family parameters (count: {paramSnapshot.Count}):");

        foreach (var (fpName, fpFormula) in paramSnapshot)
        {
            if (string.IsNullOrEmpty(fpFormula)) continue;

            var parsed = FormulaSolver.ParseSizeLookupStatic(fpFormula!);
            if (parsed is null) continue;

            var resolvedTableName = LookupColumnResolver.ResolveTableAlias(formulaByName, parsed.Value.TableName);
            if (!string.Equals(resolvedTableName, tableName, StringComparison.OrdinalIgnoreCase))
                continue;

            var queryParams = parsed.Value.QueryParameters;
            SmartConLogger.Debug($"      FamilyParam '{fpName}': queryParams=[{string.Join(", ", queryParams)}]");

            if (allQueryColumns is null)
            {
                allQueryColumns = queryParams
                    .Select((name, idx) => new CsvColumnMapping(idx + 1, name))
                    .ToList();
            }

            if (targetColIndex < 0)
            {
                for (int i = 0; i < queryParams.Count; i++)
                {
                    bool direct = string.Equals(queryParams[i], searchParamName, StringComparison.OrdinalIgnoreCase);
                    bool depends = !direct && LookupColumnResolver.DependsOn(formulaByName, queryParams[i], searchParamName);
                    SmartConLogger.Debug($"        [{i}] '{queryParams[i]}': direct={direct}, depends={depends}");
                    if (direct || depends)
                    {
                        targetColIndex = i + 1;
                        foundInParam = fpName;
                        foundViaDependsOn = depends;
                        SmartConLogger.Debug($"        → searchParam '{searchParamName}' @ queryIdx={i}, colIndex={targetColIndex}, viaDependsOn={depends}");
                        break;
                    }
                }
            }

            if (targetColIndex >= 0 && allQueryColumns is not null) break;
        }

        if (targetColIndex < 0)
        {
            var sDigits = LookupColumnResolver.ExtractTrailingDigits(searchParamName);
            if (sDigits is not null && allQueryColumns is not null)
            {
                for (int i = 0; i < allQueryColumns.Count; i++)
                {
                    if (allQueryColumns[i].ParameterName == searchParamName) continue;
                    var qDigits = LookupColumnResolver.ExtractTrailingDigits(allQueryColumns[i].ParameterName);
                    if (qDigits == sDigits)
                    {
                        targetColIndex = allQueryColumns[i].CsvColIndex;
                        foundViaDependsOn = true;
                        SmartConLogger.Debug($"        → suffix-fallback: '{searchParamName}' (suffix={sDigits}) ≈ '{allQueryColumns[i].ParameterName}' (suffix={qDigits}) @ colIndex={targetColIndex}");
                        break;
                    }
                }
            }
        }

        if (targetColIndex < 0)
        {
            SmartConLogger.Debug($"    ✗ Table '{tableName}': parameter '{searchParamName}' not found");
            return null;
        }

        SmartConLogger.Debug($"    ✓ Table '{tableName}': colIndex={targetColIndex}, found in '{foundInParam}'");
        SmartConLogger.Debug($"      AllQueryColumns: [{string.Join(", ", (allQueryColumns ?? []).Select(q => $"col[{q.CsvColIndex}]={q.ParameterName}"))}]");

        var tempPath = Path.GetTempFileName();
        try
        {
            SmartConLogger.Debug($"    → ExportSizeTable('{tableName}') to '{tempPath}'...");
            fstm.ExportSizeTable(tableName, tempPath);
            var lines = File.ReadAllLines(tempPath);
            SmartConLogger.Debug($"    → Exported {lines.Length} lines");
            SmartConLogger.DebugLines($"    CSV table '{tableName}'", lines, 30);

            bool effectiveStoresDiam = tableStoresDiameters;
            if (foundViaDependsOn && !tableStoresDiameters)
            {
                effectiveStoresDiam = true;
                SmartConLogger.Debug($"    → DependsOn match: override tableStoresDiameters=true (column stores DN, not radius)");
            }
            return new LookupContext(lines, targetColIndex, !effectiveStoresDiam, allQueryColumns ?? []);
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"    EXCEPTION ExportSizeTable: {ex.GetType().Name}: {ex.Message}");
            SmartConLogger.Error($"[LookupSvc] ExportSizeTable failed: {ex}");
            return null;
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* temp file cleanup */ }
        }
    }

    private static long RoundDnToMicrons(double radiusFt)
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
