using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SmartCon.Core.Logging;
using SmartCon.Core.Models;

namespace SmartCon.Core.Math;

/// <summary>
/// Сопоставление строк LookupTable (CSV) с FamilySymbol на основе значений
/// non-size типовых параметров. Pure-C# без зависимости от Revit API —
/// позволяет покрыть логику юнит-тестами.
/// </summary>
public static class SizeRowSymbolMatcher
{
    /// <summary>Допуск для сравнения числовых значений (в display units: мм/градусы).</summary>
    public const double NumericTolerance = 0.01;

    /// <summary>
    /// Сопоставляет CSV-строки и FamilySymbol по значениям non-size type параметров.
    /// Возвращает словарь: rowIndex → список имён всех подходящих FamilySymbol.
    /// Один row может соответствовать нескольким symbols, если у них одинаковые значения.
    /// </summary>
    /// <param name="lookupRows">Строки экспортированной CSV.</param>
    /// <param name="symbolValues">Значения параметров у каждого FamilySymbol (в display units).</param>
    /// <param name="nonSizeTypeParams">Имена non-size типовых параметров для сравнения.</param>
    /// <returns>Map rowIdx → List&lt;SymbolName&gt; (list пуст, если row ни с чем не совпал).</returns>
    public static Dictionary<int, List<string>> MatchRowsToSymbols(
        IReadOnlyList<SizeTableRow> lookupRows,
        IReadOnlyList<(string SymbolName, IReadOnlyDictionary<string, string> Values)> symbolValues,
        IReadOnlyList<string> nonSizeTypeParams)
    {
        var mapping = new Dictionary<int, List<string>>();

        if (lookupRows.Count == 0 || symbolValues.Count == 0 || nonSizeTypeParams.Count == 0)
            return mapping;

        int mismatchLogged = 0;
        const int maxMismatchLogs = 3;

        for (int rowIdx = 0; rowIdx < lookupRows.Count; rowIdx++)
        {
            var row = lookupRows[rowIdx];
            if (row.NonSizeParameterValues.Count == 0) continue;

            var matchedSymbols = new List<string>();

            foreach (var (symName, symValues) in symbolValues)
            {
                if (TryMatchRow(row.NonSizeParameterValues, symValues, nonSizeTypeParams,
                        out var mismatchReason))
                {
                    matchedSymbols.Add(symName);
                }
                else if (mismatchLogged < maxMismatchLogs && mismatchReason is not null)
                {
                    SmartConLogger.Debug($"    MapRows mismatch row[{rowIdx}]→'{symName}': {mismatchReason}");
                    mismatchLogged++;
                }
            }

            if (matchedSymbols.Count > 0)
                mapping[rowIdx] = matchedSymbols;
        }

        return mapping;
    }

    /// <summary>
    /// Определяет symbols, которые не соответствуют ни одной CSV-строке
    /// (orphan symbols — их нужно исключить из dropdown во избежание невалидных конфигураций).
    /// </summary>
    public static IReadOnlyList<string> FindOrphanSymbols(
        IReadOnlyList<(string SymbolName, IReadOnlyDictionary<string, string> Values)> symbolValues,
        Dictionary<int, List<string>> rowToSymbols)
    {
        var usedSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var names in rowToSymbols.Values)
            foreach (var n in names)
                usedSymbols.Add(n);

        return symbolValues
            .Select(sv => sv.SymbolName)
            .Where(name => !string.IsNullOrEmpty(name) && !usedSymbols.Contains(name))
            .ToList();
    }

    private static bool TryMatchRow(
        IReadOnlyDictionary<string, string> rowValues,
        IReadOnlyDictionary<string, string> symValues,
        IReadOnlyList<string> nonSizeTypeParams,
        out string? mismatchReason)
    {
        foreach (var paramName in nonSizeTypeParams)
        {
            if (!rowValues.TryGetValue(paramName, out var rowVal))
            {
                mismatchReason = $"{paramName}: row value missing";
                return false;
            }

            if (!symValues.TryGetValue(paramName, out var symVal))
            {
                mismatchReason = $"{paramName}: symbol value missing";
                return false;
            }

            if (!ValuesEquivalent(rowVal, symVal, out var delta))
            {
                mismatchReason = delta.HasValue
                    ? $"{paramName}: CSV='{rowVal}' vs Symbol='{symVal}' (Δ={delta.Value:F4})"
                    : $"{paramName}: CSV='{rowVal}' vs Symbol='{symVal}'";
                return false;
            }
        }

        mismatchReason = null;
        return true;
    }

    private static bool ValuesEquivalent(string a, string b, out double? delta)
    {
        delta = null;

        if (double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var aNum)
            && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var bNum))
        {
            var diff = System.Math.Abs(aNum - bNum);
            delta = diff;
            return diff <= NumericTolerance;
        }

        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
