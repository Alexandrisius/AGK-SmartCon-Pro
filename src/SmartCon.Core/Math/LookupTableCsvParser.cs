using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SmartCon.Core.Logging;
using SmartCon.Core.Models;

namespace SmartCon.Core.Math;

public sealed record CsvColumnMapping(int CsvColIndex, string ParameterName);

public static class LookupTableCsvParser
{
    public static bool TryParseRevitValue(string cell, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(cell)) return false;

        var clean = cell
            .Replace("\"", "")
            .Replace(" mm", "").Replace("mm", "")
            .Replace(" m", "").Replace("m", "")
            .Replace(" ft", "").Replace("ft", "")
            .Replace(" in", "").Replace("in", "")
            .Trim();

        return double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public static List<double> ExtractColumnValues(
        string[] csvLines,
        int colIndex,
        IReadOnlyList<CsvColumnMapping> allQueryColumns,
        IReadOnlyList<LookupColumnConstraint>? constraints)
    {
        var result = new List<double>();

        SmartConLogger.Debug($"    [CsvParser] ExtractColumnValues: colIndex={colIndex}, lines={csvLines.Length}, " +
            $"queryColumns={allQueryColumns.Count}, constraints={constraints?.Count ?? 0}");

        if (constraints is { Count: > 0 })
        {
            foreach (var c in constraints)
                SmartConLogger.Debug($"      constraint: connIdx={c.ConnectorIndex}, param='{c.ParameterName}', value={c.ValueMm:F1}mm");
            SmartConLogger.Debug($"      allQueryColumns: [{string.Join(", ", allQueryColumns.Select(q => $"col[{q.CsvColIndex}]='{q.ParameterName}'"))}]");
        }

        if (csvLines.Length == 0)
        {
            SmartConLogger.Debug("    CSV is empty!");
            return result;
        }

        SmartConLogger.Debug($"    CSV header (row 0): '{csvLines[0]}'");

        int parsed = 0, skipped = 0, filteredOut = 0;
        for (int row = 1; row < csvLines.Length; row++)
        {
            var line = csvLines[row];
            if (string.IsNullOrWhiteSpace(line)) { skipped++; continue; }

            var cols = line.Split(',');
            if (colIndex >= cols.Length) { skipped++; continue; }

            if (constraints is { Count: > 0 } && allQueryColumns.Count > 1)
            {
                bool rowOk = ApplyConstraintFilter(cols, colIndex, allQueryColumns, constraints, row, ref filteredOut);
                if (!rowOk) continue;
            }

            var cell = cols[colIndex].Trim().Trim('"');
            if (TryParseRevitValue(cell, out double value))
            {
                result.Add(value);
                parsed++;
            }
            else
            {
                skipped++;
            }
        }

        SmartConLogger.Debug($"    [CsvParser] → Total: parsed={parsed}, skipped={skipped}, filteredOut={filteredOut}, total={result.Count}");
        return result;
    }

    public static bool ApplyConstraintFilter(
        string[] cols,
        int colIndex,
        IReadOnlyList<CsvColumnMapping> allQueryColumns,
        IReadOnlyList<LookupColumnConstraint> constraints,
        int row,
        ref int filteredOut)
    {
        foreach (var constraint in constraints)
        {
            var colMap = allQueryColumns.FirstOrDefault(q =>
                string.Equals(q.ParameterName, constraint.ParameterName, StringComparison.OrdinalIgnoreCase));

            if (colMap is not null)
            {
                if (colMap.CsvColIndex == colIndex) continue;
                if (colMap.CsvColIndex >= cols.Length) { filteredOut++; return false; }

                var constraintCell = cols[colMap.CsvColIndex].Trim().Trim('"');
                if (!TryParseRevitValue(constraintCell, out double constraintVal)) continue;

                if (System.Math.Abs(constraintVal - constraint.ValueMm) > 0.02)
                {
                    if (filteredOut < 5)
                        SmartConLogger.Debug($"      row[{row}] FILTERED(name): col[{colMap.CsvColIndex}]='{constraintCell}'={constraintVal:F1}mm ≠ {constraint.ValueMm:F1}mm");
                    filteredOut++;
                    return false;
                }
            }
            else
            {
                bool found = false;
                bool matchesTarget = false;
                foreach (var q in allQueryColumns)
                {
                    if (q.CsvColIndex >= cols.Length) continue;
                    var cv = cols[q.CsvColIndex].Trim().Trim('"');
                    if (!TryParseRevitValue(cv, out double cVal)) continue;
                    if (System.Math.Abs(cVal - constraint.ValueMm) <= 0.5)
                    {
                        if (q.CsvColIndex == colIndex)
                            matchesTarget = true;
                        else
                        {
                            found = true;
                            break;
                        }
                    }
                }
                if (!found && !matchesTarget)
                {
                    if (filteredOut < 5)
                        SmartConLogger.Debug($"      row[{row}] FILTERED(value): constraint DN={constraint.ValueMm:F0}mm not found in any query column");
                    filteredOut++;
                    return false;
                }
            }
        }
        return true;
    }
}
