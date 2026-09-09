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

public sealed partial class RevitLookupTableService
{
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

        var nonSizeColumnIndices = new List<int>();
        for (int i = 0; i < columns.Count; i++)
        {
            if (!sizeColumnIndices.Contains(i) && columns[i].ConnectorIndices.Count == 0)
                nonSizeColumnIndices.Add(i);
        }
        if (nonSizeColumnIndices.Count > 0)
            SmartConLogger.Debug($"    nonSizeColumns=[{string.Join(",", nonSizeColumnIndices)}] ({string.Join(", ", nonSizeColumnIndices.Select(i => columns[i].ParameterName))})");

        SmartConLogger.Debug($"    sizeColumns={sizeColumnIndices.Count}/{columns.Count}, remappedTarget={remappedTargetColIndex}");

        // Получаем FamilySizeTable для доступа к unit-info колонок.
        // Используется для нормализации non-size ячеек в canonical units (мм/градусы).
        var columnUnitMap = BuildColumnUnitMap(fstm, tableName);
        if (nonSizeColumnIndices.Count > 0 && columnUnitMap.Count > 0)
        {
            var unitSummary = string.Join(", ", nonSizeColumnIndices
                .Select(i => columns[i].ParameterName)
                .Where(name => columnUnitMap.ContainsKey(name))
                .Select(name => $"{name}→{DescribeColumnUnit(columnUnitMap[name])}"));
            if (unitSummary.Length > 0)
                SmartConLogger.Debug($"    non-size units: [{unitSummary}]");
        }

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
                var sizeQueryParamNames = new List<string>();
                var sizeQueryParamValuesMm = new List<double>();
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
                    sizeQueryParamNames.Add(col.ParameterName);
                    sizeQueryParamValuesMm.Add(val);
                }

                if (connectorRadii.Count == 0)
                    connectorRadii[targetConnectorIndex] = targetRadiusFt;

                var nonSizeValues = new Dictionary<string, string>();
                foreach (var colIdx in nonSizeColumnIndices)
                {
                    var col = columns[colIdx];
                    if (col.CsvColIndex >= cols.Length) continue;
                    var cellText = cols[col.CsvColIndex].Trim().Trim('"');
                    nonSizeValues[col.ParameterName] = NormalizeCsvCellText(cellText, col.ParameterName, columnUnitMap);
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
                    QueryParamRawValuesMm = sizeQueryParamValuesMm,
                    NonSizeParameterValues = nonSizeValues
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
                .Select(kvp => $"{kvp.Key}:{kvp.Value:F8}"))
                + "|" + string.Join("|", row.NonSizeParameterValues
                    .OrderBy(kvp => kvp.Key)
                    .Select(kvp => $"{kvp.Key}={kvp.Value}"));
            if (seen.Add(key))
                result.Add(row);
        }
        return result;
    }

    /// <summary>
    /// Собирает словарь <c>columnName → FamilySizeTableColumn</c> для доступа
    /// к unit-info колонок при нормализации non-size CSV-ячеек.
    /// </summary>
    private static Dictionary<string, FamilySizeTableColumn> BuildColumnUnitMap(
        FamilySizeTableManager fstm, string tableName)
    {
        var map = new Dictionary<string, FamilySizeTableColumn>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var sizeTable = fstm.GetSizeTable(tableName);
            if (sizeTable is null) return map;

            for (int i = 0; i < sizeTable.NumberOfColumns; i++)
            {
                try
                {
                    var col = sizeTable.GetColumnHeader(i);
                    var name = col?.Name;
                    if (!string.IsNullOrEmpty(name))
                        map[name!] = col!;
                }
                catch
                {
                    // Некоторые колонки могут быть недоступны — пропускаем.
                }
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"    BuildColumnUnitMap('{tableName}') failed: {ex.Message}");
        }
        return map;
    }

    /// <summary>
    /// Нормализует текст CSV-ячейки: если ячейка — число И колонка имеет unit-info,
    /// конвертирует value в canonical units (мм/градусы) через
    /// <see cref="RevitUnitsCompat.NormalizeCellToCanonical"/>. Иначе возвращает raw text.
    /// </summary>
    private static string NormalizeCsvCellText(
        string cellText,
        string paramName,
        IReadOnlyDictionary<string, FamilySizeTableColumn> columnUnitMap)
    {
        if (!LookupTableCsvParser.TryParseRevitValue(cellText, out double cellValue))
            return cellText;

        if (!columnUnitMap.TryGetValue(paramName, out var column))
            return cellValue.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);

        var canonical = RevitUnitsCompat.NormalizeCellToCanonical(cellValue, column);
        return canonical.ToString("F6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Краткое описание unit колонки для диагностического лога.</summary>
    private static string DescribeColumnUnit(FamilySizeTableColumn column)
    {
#if REVIT2021_OR_GREATER
        try
        {
            var unit = column.GetUnitTypeId();
            return unit is null || unit.Empty() ? "-" : unit.TypeId;
        }
        catch { return "?"; }
#else
        try { return column.DisplayUnitType.ToString(); }
        catch { return "?"; }
#endif
    }
}
