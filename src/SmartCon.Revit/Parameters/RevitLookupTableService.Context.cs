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

        var binding = ConnectorSizeBindingResolver.TryGetSizeBinding(doc, connector);
        if (binding is null)
        {
            SmartConLogger.Debug("  no size binding → return null");
            return null;
        }

        var family = instance.Symbol?.Family;
        if (family is null)
        {
            SmartConLogger.Debug("  family=null → return null");
            return null;
        }

        var fstm = FamilySizeTableManager.GetFamilySizeTableManager(doc, family.Id);
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

        var snapshot = formulaCache.Get(doc, instance);
        if (snapshot is null)
        {
            SmartConLogger.Debug("  formula snapshot unavailable (EditFamily forbidden) → return null");
            return null;
        }
        var paramSnapshot = snapshot.Parameters;
        var formulaByName = snapshot.FormulaByName;
        SmartConLogger.Debug($"  Pre-cached formulaByName: {formulaByName.Count} entries from {paramSnapshot.Count} parameters");

        SmartConLogger.Debug("  → FamilyParameterAnalyzer.AnalyzeConnectorRadiusParam...");
        var (directName, rootName, formula, isInstance, isDiameter) =
            FamilyParameterAnalyzer.AnalyzeConnectorRadiusParam(
                snapshot, binding.Value.ParamName, binding.Value.IsDiameter);

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
                catch (Exception ex) { SmartConLogger.Warn($"size_lookup formula parse failed: {ex.GetType().Name}: {ex.Message}"); }

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
            SmartConLogger.Error($"ExportSizeTable failed: {ex}");
            return null;
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* temp file cleanup */ }
        }
    }
}
