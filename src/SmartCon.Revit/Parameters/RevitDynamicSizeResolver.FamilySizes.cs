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
    public IReadOnlyList<FamilySizeOption> GetAvailableFamilySizes(Document doc, ElementId elementId,
        int targetConnectorIndex)
    {
        SmartConLogger.DebugSection("RevitDynamicSizeResolver.GetAvailableFamilySizes");
        SmartConLogger.Debug($"  elementId={elementId.GetValue()}, targetConnIdx={targetConnectorIndex}");

        var element = doc.GetElement(elementId);
        if (element is null)
            return [];

        if (element is MEPCurve or FlexPipe)
        {
            var pipeSizes = GetPipeSizes(doc, element);
            return pipeSizes.Select(s => new FamilySizeOption
            {
                DisplayName = s.DisplayName,
                Radius = s.Radius,
                TargetConnectorIndex = targetConnectorIndex,
                AllConnectorRadii = new Dictionary<int, double> { [targetConnectorIndex] = s.Radius },
                Source = s.Source,
                IsAutoSelect = false
            }).ToList();
        }

        if (element is not FamilyInstance instance)
            return [];

        var options = new List<FamilySizeOption>();

        var sizeResult = _lookupTableSvc.GetAllSizeRows(doc, elementId, targetConnectorIndex);
        var lookupRows = sizeResult.Rows;
        if (lookupRows.Count > 0)
        {
            SmartConLogger.Debug($"  LookupTable: {lookupRows.Count} configs");

            var nonSizeTypeParams = new List<string>();
            var snapshot = _formulaCache.Get(doc, instance);
            if (snapshot is not null)
            {
                var cm = instance.MEPModel?.ConnectorManager;
                var connectorParamMap = new Dictionary<int, string>();
                if (cm is not null)
                {
                    foreach (Connector c in cm.Connectors)
                    {
                        if (c.ConnectorType == ConnectorType.Curve) continue;
                        var binding = ConnectorSizeBindingResolver.TryGetSizeBinding(doc, c);
                        if (binding is null) continue;
                        var (directName, rootName, _, _, _) =
                            FamilyParameterAnalyzer.AnalyzeConnectorRadiusParam(
                                snapshot, binding.Value.ParamName, binding.Value.IsDiameter);
                        var searchParam = rootName ?? directName;
                        if (searchParam is not null)
                            connectorParamMap[(int)c.Id] = searchParam;
                    }
                }
                nonSizeTypeParams = FindNonSizeTypeParameters(snapshot, sizeResult.AllNonSizeParamNames, connectorParamMap);
            }
            else
            {
                SmartConLogger.Debug("  formula snapshot unavailable (EditFamily forbidden) → nonSizeTypeParams=[]");
            }

            Dictionary<int, List<string>> rowToSymbols;

            if (nonSizeTypeParams.Count > 0)
            {
                var typeParamRows = FindRowsWithTypeParams(
                    sizeResult.PerTableRows, nonSizeTypeParams, sizeResult.ValidDnKeys);

                rowToSymbols = typeParamRows.Count > 0
                    ? MapRowsToSymbols(doc, elementId, typeParamRows, nonSizeTypeParams)
                    : MapRowsToSymbols(doc, elementId, lookupRows, nonSizeTypeParams);
            }
            else
            {
                rowToSymbols = new Dictionary<int, List<string>>();
            }

            if (nonSizeTypeParams.Count == 0 && lookupRows.Count > 0)
            {
                var placeholderOptions = lookupRows.Select(row => new FamilySizeOption
                {
                    DisplayName = string.Empty,
                    Radius = row.TargetRadiusFt,
                    TargetConnectorIndex = targetConnectorIndex,
                    AllConnectorRadii = row.ConnectorRadiiFt
                }).ToList();

                var radiiMapping = MapRowsToSymbolsByRadii(doc, elementId, placeholderOptions, targetConnectorIndex);
                foreach (var kvp in radiiMapping)
                    rowToSymbols[kvp.Key] = [kvp.Value];
            }

            if (nonSizeTypeParams.Count > 0 && rowToSymbols.Count > 0)
            {
                var dnToSymbolNames = BuildDnToSymbolNames(
                    nonSizeTypeParams.Count > 0
                        ? FindRowsWithTypeParams(sizeResult.PerTableRows, nonSizeTypeParams, sizeResult.ValidDnKeys)
                        : lookupRows,
                    rowToSymbols);

                var currentSymbolName = instance.Symbol?.Name;
                foreach (var row in lookupRows)
                {
                    var displayName = FamilySizeFormatter.BuildDisplayName(
                        row.QueryParameterRadiiFt, row.TargetColumnIndex);
                    var dnKey = RevitLookupTableService.RoundDnToMicrons(row.TargetRadiusFt);

                    if (dnToSymbolNames.TryGetValue(dnKey, out var symNames))
                    {
                        foreach (var symName in symNames)
                            options.Add(BuildOptionFromRow(row, displayName, targetConnectorIndex, symName, currentSymbolName));
                    }
                    else
                    {
                        options.Add(BuildOptionFromRow(row, displayName, targetConnectorIndex, null, currentSymbolName));
                    }
                }
            }
            else
            {
                var mappingFailed = rowToSymbols.Count == 0 && nonSizeTypeParams.Count > 0;
                if (mappingFailed && lookupRows.Count > 0)
                {
                    SmartConLogger.Warn(
                        $"Non-size type params detected ([{string.Join(", ", nonSizeTypeParams)}]) " +
                        $"but no CSV rows matched any FamilySymbol. Falling back to FamilySymbol enumeration " +
                        $"to prevent invalid DN × Symbol combinations. " +
                        $"[Action: проверьте единицы и типы параметров семейства — выпадающий список ограничен безопасным перечнем типов]");

                    var symbolConfigs = GetFamilySymbolConfigurations(doc, instance, targetConnectorIndex);
                    SmartConLogger.Debug($"  FamilySymbol safe fallback: {symbolConfigs.Count} configs");
                    options.AddRange(symbolConfigs);
                }
                else
                {
                    var currentSymbolName = instance.Symbol?.Name;

                    for (int i = 0; i < lookupRows.Count; i++)
                    {
                        var row = lookupRows[i];
                        var displayName = FamilySizeFormatter.BuildDisplayName(
                            row.QueryParameterRadiiFt, row.TargetColumnIndex);

                        var matchedSymbols = rowToSymbols.GetValueOrDefault(i);

                        if (matchedSymbols is { Count: > 0 })
                        {
                            foreach (var symName in matchedSymbols)
                                options.Add(BuildOptionFromRow(row, displayName, targetConnectorIndex, symName, currentSymbolName));
                        }
                        else
                        {
                            options.Add(BuildOptionFromRow(row, displayName, targetConnectorIndex, null, currentSymbolName));
                        }
                    }
                }
            }
        }
        else
        {
            var symbolConfigs = GetFamilySymbolConfigurations(doc, instance, targetConnectorIndex);
            SmartConLogger.Debug($"  FamilySymbol fallback: {symbolConfigs.Count} configs");
            options.AddRange(symbolConfigs);
        }

        var deduped = FamilySizeFormatter.DeduplicateFamilyOptions(options);
        var sorted = SortByTargetDn(deduped);
        sorted = FamilySizeFormatter.AppendSymbolNameSuffix(sorted);

        SmartConLogger.Debug($"  → {sorted.Count} unique configs (sorted)");
        return sorted;
    }

    private static FamilySizeOption BuildOptionFromRow(
        SizeTableRow row,
        string displayName,
        int targetConnectorIndex,
        string? symbolName,
        string? currentSymbolName)
    {
        return new FamilySizeOption
        {
            DisplayName = displayName,
            Radius = row.TargetRadiusFt,
            TargetConnectorIndex = targetConnectorIndex,
            AllConnectorRadii = row.ConnectorRadiiFt,
            QueryParameterRadiiFt = row.QueryParameterRadiiFt,
            UniqueParameterCount = row.UniqueQueryParameterCount,
            TargetColumnIndex = row.TargetColumnIndex,
            QueryParamConnectorGroups = row.QueryParamConnectorGroups,
            QueryParamNames = row.QueryParamNames,
            QueryParamRawValuesMm = row.QueryParamRawValuesMm,
            NonSizeParameterValues = row.NonSizeParameterValues,
            Source = "LookupTable",
            IsAutoSelect = false,
            SymbolName = symbolName,
            CurrentSymbolName = currentSymbolName
        };
    }

    private List<FamilySizeOption> GetFamilySymbolConfigurations(Document doc, FamilyInstance instance, int targetConnectorIndex)
    {
        var family = instance.Symbol?.Family;
        if (family is null) return [];

        var currentSymbolId = instance.Symbol?.Id;
        SmartConLogger.Debug($"  FamilySymbol configs: '{family.Name}', current={currentSymbolId?.GetValue()}");

        var symbolData = _sizeExtractor.GetSymbolConnectorRadii(doc, instance.Id, targetConnectorIndex);
        var sharedParamGroups = FamilySymbolSizeExtractor.AnalyzeSharedParameterGroups(symbolData);

        var configs = new List<FamilySizeOption>();
        foreach (var (symbolId, connectorRadii, symbolName) in symbolData)
        {
            var displayRadii = FamilySymbolSizeExtractor.BuildDisplayRadii(connectorRadii, sharedParamGroups, targetConnectorIndex);
            var displayName = FamilySizeFormatter.BuildDisplayNameLegacy(displayRadii, targetConnectorIndex);

            configs.Add(new FamilySizeOption
            {
                DisplayName = displayName,
                Radius = connectorRadii.GetValueOrDefault(targetConnectorIndex, 0),
                TargetConnectorIndex = targetConnectorIndex,
                AllConnectorRadii = connectorRadii,
                Source = "FamilySymbol",
                IsAutoSelect = false,
                SymbolName = symbolName,
                CurrentSymbolName = currentSymbolId is not null
                    ? doc.GetElement(currentSymbolId)?.Name
                    : null
            });
        }

        return configs;
    }

    private static List<string> FindNonSizeTypeParameters(
        FamilyParameterSnapshot snapshot,
        IEnumerable<string> allNonSizeParamNames,
        Dictionary<int, string> connectorParamMap)
    {
        var nonSizeParamNames = allNonSizeParamNames as HashSet<string>
            ?? new HashSet<string>(allNonSizeParamNames, StringComparer.OrdinalIgnoreCase);
        if (nonSizeParamNames.Count == 0) return [];

        var formulaByName = snapshot.FormulaByName;

        var leafParams = new List<string>();
        foreach (var nsp in allNonSizeParamNames)
        {
            var leaf = FindLeafParameter(nsp, formulaByName);
            if (leaf is null) continue;

            bool found = snapshot.IsInstanceByName.TryGetValue(leaf, out bool isInstance);
            if (found && !isInstance)
                leafParams.Add(leaf);
        }

        if (leafParams.Count > 0)
            SmartConLogger.Debug($"  FindNonSizeTypeParameters: leaf type params=[{string.Join(", ", leafParams)}]");

        return leafParams;
    }

    private static string? FindLeafParameter(
        string paramName,
        IReadOnlyDictionary<string, string> formulaByName)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = paramName;

        while (true)
        {
            if (!visited.Add(current)) return null;

            if (!formulaByName.TryGetValue(current, out var formula))
                return current;

            var trimmed = formula.Trim();
            if (trimmed.StartsWith("\"") && trimmed.EndsWith("\""))
                return null;

            var vars = FormulaSolver.ExtractVariablesStatic(formula);
            if (vars.Count == 0) return current;
            if (vars.Count > 1) return null;

            current = vars[0];
        }
    }

    private static List<SizeTableRow> FindRowsWithTypeParams(
        IReadOnlyList<IReadOnlyList<SizeTableRow>> perTableRows,
        IReadOnlyList<string> nonSizeTypeParams,
        IEnumerable<long> validDnKeys)
    {
        var validDn = validDnKeys as HashSet<long> ?? new HashSet<long>(validDnKeys);
        var typeParamSet = new HashSet<string>(nonSizeTypeParams, StringComparer.OrdinalIgnoreCase);

        foreach (var tableRows in perTableRows)
        {
            if (tableRows.Count == 0) continue;

            var rowParams = tableRows[0].NonSizeParameterValues.Keys;
            bool containsAll = true;
            foreach (var tp in nonSizeTypeParams)
            {
                bool found = false;
                foreach (var rp in rowParams)
                {
                    if (string.Equals(rp, tp, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found) { containsAll = false; break; }
            }

            if (!containsAll) continue;

            SmartConLogger.Debug(
                $"  FindRowsWithTypeParams: found table with {tableRows.Count} rows " +
                $"containing type params [{string.Join(", ", nonSizeTypeParams)}]");

            if (validDn.Count > 0)
            {
                var filtered = tableRows
                    .Where(r => validDn.Contains(RevitLookupTableService.RoundDnToMicrons(r.TargetRadiusFt)))
                    .ToList();
                SmartConLogger.Debug($"  FindRowsWithTypeParams: filtered to {filtered.Count} rows by validDn");
                return filtered;
            }

            return tableRows.ToList();
        }

        SmartConLogger.Debug(
            $"  FindRowsWithTypeParams: no table found containing all type params [{string.Join(", ", nonSizeTypeParams)}]");
        return [];
    }
}
