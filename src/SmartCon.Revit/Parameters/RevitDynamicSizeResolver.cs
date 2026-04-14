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
using SmartCon.Revit.Extensions;
using SmartCon.Core;


using static SmartCon.Core.Units;
namespace SmartCon.Revit.Parameters;

public sealed class RevitDynamicSizeResolver : IDynamicSizeResolver
{
    private readonly ILookupTableService _lookupTableSvc;
    private readonly FamilySymbolSizeExtractor _sizeExtractor;

    public RevitDynamicSizeResolver(ILookupTableService lookupTableSvc, FamilySymbolSizeExtractor sizeExtractor)
    {
        _lookupTableSvc = lookupTableSvc;
        _sizeExtractor = sizeExtractor;
    }

    public IReadOnlyList<SizeOption> GetAvailableSizes(Document doc, ElementId elementId,
        int connectorIndex,
        IReadOnlyList<LookupColumnConstraint>? constraints = null)
    {
        SmartConLogger.DebugSection("RevitDynamicSizeResolver.GetAvailableSizes");
        SmartConLogger.Debug($"  elementId={elementId.Value}, connIdx={connectorIndex}, constraints={constraints?.Count ?? 0}");

        var element = doc.GetElement(elementId);
        if (element is null)
        {
            SmartConLogger.Debug("  element=null → return []");
            return [];
        }

        SmartConLogger.Debug($"  element='{element.Name}' ({element.GetType().Name})");

        if (element is MEPCurve mepCurve)
        {
            SmartConLogger.Debug("  → MEPCurve: GetPipeSizes");
            var result = GetPipeSizes(doc, mepCurve);
            SmartConLogger.Debug($"  → found {result.Count} sizes");
            return result;
        }

        if (element is FlexPipe)
        {
            SmartConLogger.Debug("  → FlexPipe: GetPipeSizes");
            var result = GetPipeSizes(doc, element);
            SmartConLogger.Debug($"  → found {result.Count} sizes");
            return result;
        }

        if (element is not FamilyInstance instance)
        {
            SmartConLogger.Debug($"  not FamilyInstance ({element.GetType().Name}) → return []");
            return [];
        }

        SmartConLogger.Debug($"  family='{instance.Symbol?.Family?.Name}', symbol='{instance.Symbol?.Name}'");

        var sizes = TryGetLookupTableSizes(doc, elementId, connectorIndex, constraints);
        if (sizes.Count > 0)
        {
            SmartConLogger.Debug($"  → LookupTable: {sizes.Count} sizes");
            return sizes;
        }

        SmartConLogger.Debug("  → LookupTable empty, fallback to FamilySymbol enumeration");
        var symbolSizes = GetFamilySymbolSizes(doc, instance, connectorIndex);
        SmartConLogger.Debug($"  → FamilySymbol: {symbolSizes.Count} sizes");
        return symbolSizes;
    }

    private List<SizeOption> GetPipeSizes(Document doc, Element element)
    {
        var result = new List<SizeOption>();

        var pipeType = element.Document.GetElement(element.GetTypeId()) as ElementType;
        if (pipeType is null) return [];

        var collector = new FilteredElementCollector(doc)
            .OfClass(typeof(PipeType))
            .Cast<PipeType>();

        var diameters = new SortedSet<double>();
        foreach (var pt in collector)
        {
            var param = pt.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
            if (param is not null && param.HasValue)
            {
                diameters.Add(param.AsDouble());
            }
        }

        if (diameters.Count == 0)
        {
            var currentParam = element.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
            if (currentParam is not null && currentParam.HasValue)
            {
                diameters.Add(currentParam.AsDouble());
                SmartConLogger.Debug($"  GetPipeSizes: fallback to current diameter={currentParam.AsDouble() * FeetToMm:F2} mm");
            }
        }
        else
        {
            SmartConLogger.Debug($"  GetPipeSizes: {diameters.Count} PipeType sizes");
        }

        foreach (var diam in diameters)
        {
            var radius = diam / 2.0;
            var dn = Math.Round(radius * 2.0 * FeetToMm);
            result.Add(new SizeOption
            {
                DisplayName = $"DN {dn}",
                Radius = radius,
                Source = "PipeType"
            });
        }

        return result;
    }

    private List<SizeOption> TryGetLookupTableSizes(Document doc, ElementId elementId,
        int connectorIndex,
        IReadOnlyList<LookupColumnConstraint>? constraints)
    {
        SmartConLogger.DebugSection("RevitDynamicSizeResolver.TryGetLookupTableSizes");

        var element = doc.GetElement(elementId) as FamilyInstance;
        if (element is null)
        {
            SmartConLogger.Debug("  element=null or not FamilyInstance → return []");
            return [];
        }

        return EditFamilySession.Run(doc, element,
            familyDoc => ExtractSizesFromFamily(familyDoc, element, connectorIndex, constraints))
            ?? [];
    }

    private List<SizeOption> ExtractSizesFromFamily(Document familyDoc, FamilyInstance instance,
        int connectorIndex,
        IReadOnlyList<LookupColumnConstraint>? constraints)
    {
        SmartConLogger.DebugSection("RevitDynamicSizeResolver.ExtractSizesFromFamily");

        var fstm = FamilySizeTableManager.GetFamilySizeTableManager(
            familyDoc, familyDoc.OwnerFamily.Id);

        if (fstm is null || fstm.NumberOfSizeTables == 0)
        {
            SmartConLogger.Debug("  FamilySizeTableManager=null or empty → return []");
            return [];
        }

        SmartConLogger.Debug($"  FamilySizeTableManager: {fstm.NumberOfSizeTables} tables");

        var snapshot = FamilyParameterSnapshot.Build(familyDoc.FamilyManager);
        var paramSnapshot = snapshot.Parameters;
        var formulaByName = snapshot.FormulaByName;
        SmartConLogger.Debug($"  Pre-cached formulaByName: {formulaByName.Count} entries from {paramSnapshot.Count} parameters");

        var cm = instance.MEPModel?.ConnectorManager;
        var connector = cm?.FindByIndex(connectorIndex);
        if (connector is null)
        {
            SmartConLogger.Debug($"  connector[{connectorIndex}]=null → return []");
            return [];
        }

        var targetOrigin = connector.CoordinateSystem.Origin;
        var instanceTransform = instance.GetTransform();

        var (directName, rootName, formula, _, isDiameter) =
            FamilyParameterAnalyzer.AnalyzeConnectorRadiusParam(
                familyDoc, instanceTransform, targetOrigin,
                instance.HandFlipped, instance.FacingFlipped);

        SmartConLogger.Debug($"  FPA: directName='{directName}', rootName='{rootName}', formula='{formula}', isDiameter={isDiameter}");

        var searchParamName = rootName ?? directName;
        if (searchParamName is null)
        {
            SmartConLogger.Debug("  searchParamName=null → return []");
            return [];
        }

        bool tableStoresDiameters;
        if (rootName is not null && formula is not null)
        {
            double refRadius = 1.0;
            double directRef = isDiameter ? refRadius * 2.0 : refRadius;
            var rootRef = FormulaSolver.SolveForStatic(formula, rootName, directRef);
            if (rootRef.HasValue)
            {
                tableStoresDiameters = Math.Abs(rootRef.Value / refRadius - 2.0) < 0.1;
                SmartConLogger.Debug($"  tableStoresDiameters={tableStoresDiameters} (SolveFor rootRef={rootRef.Value:F3})");
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
                catch (Exception ex) { SmartConLogger.Warn($"[DynSizeResolver] size_lookup formula parse failed: {ex.GetType().Name}: {ex.Message}"); }

                tableStoresDiameters = isQueryParam || isDiameter;
                SmartConLogger.Debug($"  tableStoresDiameters={tableStoresDiameters} (SolveFor=null, isQueryParam={isQueryParam}, isDiameter={isDiameter})");
            }
        }
        else
        {
            tableStoresDiameters = isDiameter;
            SmartConLogger.Debug($"  tableStoresDiameters={tableStoresDiameters} (from FPA)");
        }

        var tableNames = fstm.GetAllSizeTableNames().ToList();
        SmartConLogger.Debug($"  Tables: [{string.Join(", ", tableNames)}]");

        var perTableConstrained = new List<SortedSet<double>>();
        var perTableUnconstrained = new List<SortedSet<double>>();

        foreach (var tableName in tableNames)
        {
            var (colIndex, allQueryColumns, viaDependsOn) = LookupColumnResolver.FindColumnIndex(tableName, searchParamName, paramSnapshot, formulaByName);
            if (colIndex < 0)
            {
                SmartConLogger.Debug($"  Table '{tableName}': colIndex not found → skip");
                continue;
            }

            bool effectiveStoresDiam = tableStoresDiameters;
            if (viaDependsOn && !tableStoresDiameters)
            {
                effectiveStoresDiam = true;
                SmartConLogger.Debug($"  DependsOn match: override tableStoresDiameters=true (column stores DN, not radius)");
            }

            bool isMultiColumn = allQueryColumns.Count > 1;
            SmartConLogger.Debug($"  Table '{tableName}': colIndex={colIndex}, queryColumns={allQueryColumns.Count}, multiColumn={isMultiColumn}, storesDiam={effectiveStoresDiam}");

            var tempPath = Path.GetTempFileName();
            try
            {
                fstm.ExportSizeTable(tableName, tempPath);
                var lines = File.ReadAllLines(tempPath);
                var values = ExtractRadiusValues(lines, colIndex, effectiveStoresDiam, allQueryColumns, constraints);
                SmartConLogger.Debug($"  Exported {values.Count} values from table '{tableName}'");

                if (values.Count > 0)
                {
                    var set = new SortedSet<double>(values);
                    if (isMultiColumn && constraints is { Count: > 0 })
                        perTableConstrained.Add(set);
                    else
                        perTableUnconstrained.Add(set);
                }
            }
            catch (Exception ex)
            {
                SmartConLogger.Debug($"  EXCEPTION ExportSizeTable: {ex.Message}");
            }
            finally
            {
                try { File.Delete(tempPath); } catch { /* temp file cleanup */ }
            }
        }

        var constrainedValues = IntersectRadiusSets(perTableConstrained);
        var unconstrainedValues = IntersectRadiusSets(perTableUnconstrained);

        var allValues = constrainedValues.Count > 0 ? constrainedValues : unconstrainedValues;
        if (constrainedValues.Count > 0 && unconstrainedValues.Count > 0)
            SmartConLogger.Debug($"  [MultiCol] Multi-column priority: {constrainedValues.Count} constrained, {unconstrainedValues.Count} unconstrained → using constrained");

        var result = new List<SizeOption>();
        foreach (var radius in allValues)
        {
            var dn = Math.Round(radius * 2.0 * FeetToMm);
            result.Add(new SizeOption
            {
                DisplayName = $"DN {dn}",
                Radius = radius,
                Source = "LookupTable"
            });
        }

        SmartConLogger.Debug($"  Total: {result.Count} unique sizes from LookupTable");
        return result;
    }

    private static SortedSet<double> IntersectRadiusSets(List<SortedSet<double>> sets)
    {
        if (sets.Count == 0) return [];
        if (sets.Count == 1) return sets[0];

        var result = new SortedSet<double>(sets[0]);
        for (int i = 1; i < sets.Count; i++)
        {
            var keep = new SortedSet<double>();
            foreach (var v in result)
            {
                if (sets[i].Any(s => Math.Abs(s - v) < 1e-6))
                    keep.Add(v);
            }
            result = keep;
        }

        SmartConLogger.Debug($"  [Intersect] {sets.Count} sets, [{string.Join(" ∩ ", sets.Select(s => s.Count))}] → {result.Count}");
        return result;
    }

    private static List<double> ExtractRadiusValues(
        string[] csvLines,
        int colIndex,
        bool tableStoresDiameters,
        IReadOnlyList<CsvColumnMapping> allQueryColumns,
        IReadOnlyList<LookupColumnConstraint>? constraints)
    {
        var rawValues = LookupTableCsvParser.ExtractColumnValues(csvLines, colIndex, allQueryColumns, constraints);
        var result = new List<double>();

        foreach (var valueMm in rawValues)
        {
            double radius = tableStoresDiameters ? valueMm / 2.0 : valueMm;
            double radiusInFeet = radius * MmToFeet;
            result.Add(radiusInFeet);
        }

        if (constraints is { Count: > 0 })
            SmartConLogger.Info($"[MultiCol] Dropdown filtering: {rawValues.Count} raw values, {result.Count} radii extracted");

        return result;
    }

    private List<SizeOption> GetFamilySymbolSizes(Document doc, FamilyInstance instance, int connectorIndex)
    {
        SmartConLogger.DebugSection("RevitDynamicSizeResolver.GetFamilySymbolSizes");

        var radii = _sizeExtractor.GetSymbolRadii(doc, instance.Id, connectorIndex);

        var result = new List<SizeOption>();
        foreach (var radius in radii)
        {
            var dn = Math.Round(radius * 2.0 * FeetToMm);
            result.Add(new SizeOption
            {
                DisplayName = $"DN {dn}",
                Radius = radius,
                Source = "FamilySymbol"
            });
        }

        SmartConLogger.Debug($"  Total: {result.Count} unique sizes from FamilySymbol");
        return result;
    }

    public IReadOnlyList<FamilySizeOption> GetAvailableFamilySizes(Document doc, ElementId elementId,
        int targetConnectorIndex)
    {
        SmartConLogger.DebugSection("RevitDynamicSizeResolver.GetAvailableFamilySizes");
        SmartConLogger.Debug($"  elementId={elementId.Value}, targetConnIdx={targetConnectorIndex}");

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

        var lookupRows = _lookupTableSvc.GetAllSizeRows(doc, elementId, targetConnectorIndex);
        if (lookupRows.Count > 0)
        {
            SmartConLogger.Debug($"  LookupTable: {lookupRows.Count} configs");
            foreach (var row in lookupRows)
            {
                var displayName = FamilySizeFormatter.BuildDisplayName(
                    row.QueryParameterRadiiFt, row.TargetColumnIndex);
                options.Add(new FamilySizeOption
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
                    Source = "LookupTable",
                    IsAutoSelect = false
                });
            }
        }
        else
        {
            var symbolConfigs = GetFamilySymbolConfigurations(doc, instance, targetConnectorIndex);
            SmartConLogger.Debug($"  FamilySymbol fallback: {symbolConfigs.Count} configs");
            options.AddRange(symbolConfigs);
        }

        var deduped = DeduplicateFamilyOptions(options);
        var sorted = SortByTargetDn(deduped);
        SmartConLogger.Debug($"  → {sorted.Count} unique configs (sorted)");
        return sorted;
    }

    private List<FamilySizeOption> GetFamilySymbolConfigurations(Document doc, FamilyInstance instance, int targetConnectorIndex)
    {
        var family = instance.Symbol?.Family;
        if (family is null) return [];

        var currentSymbolId = instance.Symbol?.Id;
        SmartConLogger.Debug($"  FamilySymbol configs: '{family.Name}', current={currentSymbolId?.Value}");

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

    private static List<FamilySizeOption> DeduplicateFamilyOptions(List<FamilySizeOption> options)
    {
        var seen = new HashSet<string>();
        var result = new List<FamilySizeOption>();
        foreach (var opt in options)
        {
            var key = string.Join("|", opt.AllConnectorRadii
                .OrderBy(kvp => kvp.Key)
                .Select(kvp => $"{kvp.Key}:{kvp.Value:F8}"));
            if (seen.Add(key))
                result.Add(opt);
        }
        return result;
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
