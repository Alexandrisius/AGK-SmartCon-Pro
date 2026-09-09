using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Logging;
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

public sealed partial class RevitDynamicSizeResolver
{
    private static Dictionary<long, HashSet<string>> BuildDnToSymbolNames(
        IReadOnlyList<SizeTableRow> typeParamRows,
        Dictionary<int, List<string>> rowToSymbols)
    {
        var result = new Dictionary<long, HashSet<string>>();

        for (int i = 0; i < typeParamRows.Count; i++)
        {
            var symbols = rowToSymbols.GetValueOrDefault(i);
            if (symbols is null || symbols.Count == 0) continue;

            var dnKey = RevitLookupTableService.RoundDnToMicrons(typeParamRows[i].TargetRadiusFt);
            if (!result.TryGetValue(dnKey, out var set))
            {
                set = new HashSet<string>();
                result[dnKey] = set;
            }
            foreach (var s in symbols)
                set.Add(s);
        }

        SmartConLogger.Debug(
            $"  BuildDnToSymbolNames: {result.Count} DN keys mapped, " +
            $"total symbol associations: {result.Sum(kv => kv.Value.Count)}");

        return result;
    }

    private Dictionary<int, List<string>> MapRowsToSymbols(
        Document doc,
        ElementId instanceId,
        IReadOnlyList<SizeTableRow> lookupRows,
        IReadOnlyList<string> nonSizeTypeParams)
    {
        if (nonSizeTypeParams.Count == 0 || lookupRows.Count == 0) return [];

        var instance = doc.GetElement(instanceId) as FamilyInstance;
        if (instance is null) return [];

        var family = instance.Symbol?.Family;
        if (family is null) return [];

        var symbolIds = family.GetFamilySymbolIds().ToList();
        if (symbolIds.Count == 0) return [];

        var symbolParamValues = new List<(string SymbolName, IReadOnlyDictionary<string, string> Values)>();

        _transactionService.RunAndRollback("SmartCon_MapRows", txDoc =>
        {
            foreach (var symbolId in symbolIds)
            {
                try
                {
                    var inst = txDoc.GetElement(instanceId) as FamilyInstance;
                    if (inst is null) continue;

                    inst.ChangeTypeId(symbolId);
                    txDoc.Regenerate();

                    var sym = txDoc.GetElement(symbolId) as FamilySymbol;
                    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var paramName in nonSizeTypeParams)
                    {
                        var param = inst.LookupParameter(paramName)
                                    ?? sym?.LookupParameter(paramName);
                        if (param is null) continue;

                        // Конверсия internal units → display units (мм/градусы),
                        // совместимых с форматом CSV, экспортируемым Revit.
                        values[paramName] = RevitUnitsCompat.ReadParamValueAsCsvCompatibleString(param);
                    }

                    var symName = sym?.Name ?? string.Empty;
                    symbolParamValues.Add((symName, values));
                    SmartConLogger.Debug(
                        $"  FamilyType '{symName}': [{string.Join(", ", values.Select(kv => $"{kv.Key}={kv.Value}"))}]");
                }
                catch (Exception ex)
                {
                    SmartConLogger.Debug($"  MapRowsToSymbols: error for symbolId={symbolId.GetValue()}: {ex.Message}");
                }
            }
        });

        var mapping = SizeRowSymbolMatcher.MatchRowsToSymbols(lookupRows, symbolParamValues, nonSizeTypeParams);

        int totalRowsWithNonSize = lookupRows.Count(r => r.NonSizeParameterValues.Count > 0);
        int mappedRows = mapping.Count;
        int multiMappedRows = mapping.Count(kvp => kvp.Value.Count > 1);

        if (mappedRows > 0)
        {
            SmartConLogger.Debug(
                $"  MapRowsToSymbols: mapped {mappedRows}/{totalRowsWithNonSize} rows to symbols " +
                $"(multi-mapped: {multiMappedRows}, params=[{string.Join(", ", nonSizeTypeParams)}])");
        }
        else
        {
            SmartConLogger.Debug(
                $"  MapRowsToSymbols: no matches! symbols={symbolParamValues.Count}, " +
                $"rowsWithNonSize={totalRowsWithNonSize}, params=[{string.Join(", ", nonSizeTypeParams)}]");
        }

        var orphans = SizeRowSymbolMatcher.FindOrphanSymbols(symbolParamValues, mapping);
        if (orphans.Count > 0)
        {
            SmartConLogger.Warn(
                $"Orphan symbols (no matching CSV row, excluded from dropdown): " +
                $"[{string.Join(", ", orphans)}] " +
                $"[Action: проверьте lookup-таблицу семейства — этим типам нет строки в CSV, при необходимости дополните таблицу]");
        }

        return mapping;
    }

    private Dictionary<int, string> MapRowsToSymbolsByRadii(
        Document doc,
        ElementId instanceId,
        IReadOnlyList<FamilySizeOption> options,
        int targetConnectorIndex)
    {
        var instance = doc.GetElement(instanceId) as FamilyInstance;
        if (instance is null) return [];

        var family = instance.Symbol?.Family;
        if (family is null) return [];

        var symbolIds = family.GetFamilySymbolIds().ToList();
        if (symbolIds.Count <= 1) return [];

        var symbolData = _sizeExtractor.GetSymbolConnectorRadii(doc, instanceId, targetConnectorIndex);
        if (symbolData.Count <= 1) return [];

        var allConnIds = new HashSet<int>();
        foreach (var (_, radii, _) in symbolData)
            foreach (var connId in radii.Keys)
                allConnIds.Add(connId);

        var typeControlledConnectors = new HashSet<int>();
        foreach (var connId in allConnIds)
        {
            var values = symbolData
                .Select(sd => sd.ConnectorRadii.GetValueOrDefault(connId, -1.0))
                .ToList();
            if (values.Distinct().Count() > 1)
                typeControlledConnectors.Add(connId);
        }

        if (typeControlledConnectors.Count == 0)
        {
            SmartConLogger.Debug($"  MapRowsToSymbolsByRadii: no type-controlled connectors found (all radii identical across {symbolData.Count} symbols)");
            return [];
        }

        SmartConLogger.Debug($"  MapRowsToSymbolsByRadii: type-controlled connectors=[{string.Join(", ", typeControlledConnectors)}] across {symbolData.Count} symbols");

        const double eps = 1e-6;
        var mapping = new Dictionary<int, string>();

        for (int rowIdx = 0; rowIdx < options.Count; rowIdx++)
        {
            var opt = options[rowIdx];
            if (opt.AllConnectorRadii.Count == 0) continue;

            foreach (var (_, symbolRadii, symbolName) in symbolData)
            {
                bool match = true;
                foreach (var connId in typeControlledConnectors)
                {
                    if (!opt.AllConnectorRadii.TryGetValue(connId, out var rowRadius)) { match = false; break; }
                    if (!symbolRadii.TryGetValue(connId, out var symRadius)) { match = false; break; }
                    if (Math.Abs(rowRadius - symRadius) > eps) { match = false; break; }
                }

                if (match)
                {
                    mapping[rowIdx] = symbolName;
                    break;
                }
            }
        }

        if (mapping.Count > 0)
            SmartConLogger.Debug($"  MapRowsToSymbolsByRadii: mapped {mapping.Count}/{options.Count} rows via type-controlled connector radii");

        return mapping;
    }

    private static List<FamilySizeOption> SortByTargetDn(List<FamilySizeOption> options)
    {
        if (options.Count <= 1) return options;

        return options
            .OrderBy(o => FamilySizeFormatter.ToDn(o.Radius))
            .ThenBy(o =>
            {
                if (o.QueryParameterRadiiFt.Count <= 1) return 0;
                int targetIdx = o.TargetColumnIndex - 1;
                if (targetIdx < 0 || targetIdx >= o.QueryParameterRadiiFt.Count) targetIdx = 0;
                var others = o.QueryParameterRadiiFt
                    .Where((_, i) => i != targetIdx)
                    .Select(FamilySizeFormatter.ToDn)
                    .ToList();
                return others.Count > 0 ? others[0] : 0;
            })
            .ThenBy(o =>
            {
                if (o.QueryParameterRadiiFt.Count <= 2) return 0;
                int targetIdx = o.TargetColumnIndex - 1;
                if (targetIdx < 0 || targetIdx >= o.QueryParameterRadiiFt.Count) targetIdx = 0;
                var others = o.QueryParameterRadiiFt
                    .Where((_, i) => i != targetIdx)
                    .Select(FamilySizeFormatter.ToDn)
                    .ToList();
                return others.Count > 1 ? others[1] : 0;
            })
            .ToList();
    }
}
