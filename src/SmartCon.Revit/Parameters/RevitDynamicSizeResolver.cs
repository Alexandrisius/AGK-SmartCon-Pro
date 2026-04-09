using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Logging;
using SmartCon.Core.Math.FormulaEngine.Solver;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Extensions;
using RevitFamily = Autodesk.Revit.DB.Family;

namespace SmartCon.Revit.Parameters;

/// <summary>
/// Получение списка доступных размеров DN для динамического семейства.
/// Сначала пробует LookupTable, если нет — перебирает все FamilySymbol.
/// Вызывать ВНЕ транзакции (EditFamily требует doc.IsModifiable == false).
/// </summary>
public sealed class RevitDynamicSizeResolver : IDynamicSizeResolver
{
    private readonly ILookupTableService _lookupTableSvc;

    public RevitDynamicSizeResolver(ILookupTableService lookupTableSvc)
    {
        _lookupTableSvc = lookupTableSvc;
    }

    public IReadOnlyList<SizeOption> GetAvailableSizes(Document doc, ElementId elementId,
        int connectorIndex,
        IReadOnlyList<LookupColumnConstraint>? constraints = null)
    {
        SmartConLogger.LookupSection("RevitDynamicSizeResolver.GetAvailableSizes");
        SmartConLogger.Lookup($"  elementId={elementId.Value}, connIdx={connectorIndex}, constraints={constraints?.Count ?? 0}");

        var element = doc.GetElement(elementId);
        if (element is null)
        {
            SmartConLogger.Lookup("  element=null → return []");
            return [];
        }

        SmartConLogger.Lookup($"  element='{element.Name}' ({element.GetType().Name})");

        if (element is MEPCurve mepCurve)
        {
            SmartConLogger.Lookup("  → MEPCurve: GetPipeSizes");
            var result = GetPipeSizes(doc, mepCurve);
            SmartConLogger.Lookup($"  → найдено {result.Count} размеров");
            return result;
        }

        if (element is FlexPipe)
        {
            SmartConLogger.Lookup("  → FlexPipe: GetPipeSizes");
            var result = GetPipeSizes(doc, element);
            SmartConLogger.Lookup($"  → найдено {result.Count} размеров");
            return result;
        }

        if (element is not FamilyInstance instance)
        {
            SmartConLogger.Lookup($"  not FamilyInstance ({element.GetType().Name}) → return []");
            return [];
        }

        SmartConLogger.Lookup($"  family='{instance.Symbol?.Family?.Name}', symbol='{instance.Symbol?.Name}'");

        var sizes = TryGetLookupTableSizes(doc, elementId, connectorIndex, constraints);
        if (sizes.Count > 0)
        {
            SmartConLogger.Lookup($"  → LookupTable: {sizes.Count} размеров");
            return sizes;
        }

        SmartConLogger.Lookup("  → LookupTable пуст, fallback на FamilySymbol перебор");
        var symbolSizes = GetFamilySymbolSizes(doc, instance, connectorIndex);
        SmartConLogger.Lookup($"  → FamilySymbol: {symbolSizes.Count} размеров");
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
                SmartConLogger.Lookup($"  GetPipeSizes: fallback на текущий диаметр={currentParam.AsDouble() * 304.8:F2} мм");
            }
        }
        else
        {
            SmartConLogger.Lookup($"  GetPipeSizes: {diameters.Count} PipeType размеров");
        }

        foreach (var diam in diameters)
        {
            var radius = diam / 2.0;
            var dn = Math.Round(radius * 2.0 * 304.8);
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
        SmartConLogger.LookupSection("RevitDynamicSizeResolver.TryGetLookupTableSizes");

        if (doc.IsModifiable)
        {
            SmartConLogger.Lookup("  doc.IsModifiable=true → EditFamily запрещён → return []");
            return [];
        }

        var element = doc.GetElement(elementId) as FamilyInstance;
        if (element is null)
        {
            SmartConLogger.Lookup("  element=null или не FamilyInstance → return []");
            return [];
        }

        var family = element.Symbol?.Family;
        if (family is null)
        {
            SmartConLogger.Lookup("  family=null → return []");
            return [];
        }

        SmartConLogger.Lookup($"  family='{family.Name}', EditFamily...");

        try
        {
            var familyDoc = doc.EditFamily(family);
            if (familyDoc is null)
            {
                SmartConLogger.Lookup("  EditFamily вернул null → return []");
                return [];
            }

            try
            {
                return ExtractSizesFromFamily(familyDoc, element, connectorIndex, constraints);
            }
            finally
            {
                familyDoc.Close(false);
                SmartConLogger.Lookup("  familyDoc закрыт");
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Lookup($"  ИСКЛЮЧЕНИЕ EditFamily: {ex.GetType().Name}: {ex.Message}");
            SmartConLogger.Warn($"[SizeResolver] EditFamily failed: {ex.Message}");
            return [];
        }
    }

    private List<SizeOption> ExtractSizesFromFamily(Document familyDoc, FamilyInstance instance,
        int connectorIndex,
        IReadOnlyList<LookupColumnConstraint>? constraints)
    {
        SmartConLogger.LookupSection("RevitDynamicSizeResolver.ExtractSizesFromFamily");

        var fstm = FamilySizeTableManager.GetFamilySizeTableManager(
            familyDoc, familyDoc.OwnerFamily.Id);

        if (fstm is null || fstm.NumberOfSizeTables == 0)
        {
            SmartConLogger.Lookup("  FamilySizeTableManager=null или пуст → return []");
            return [];
        }

        SmartConLogger.Lookup($"  FamilySizeTableManager: {fstm.NumberOfSizeTables} таблиц");

        var fm = familyDoc.FamilyManager;

        // PRE-CACHE: материализуем fm.Parameters ДО вызова FPA,
        // чтобы избежать COM-коррупции ParameterSet после AssociatedParameters доступа
        var paramSnapshot = new List<(string? Name, string? Formula)>();
        var formulaByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (FamilyParameter fp in fm.Parameters)
        {
            var fpName = fp.Definition?.Name;
            var fpFormula = fp.Formula;
            paramSnapshot.Add((fpName, fpFormula));
            if (fpName != null && !string.IsNullOrEmpty(fpFormula))
                formulaByName.TryAdd(fpName, fpFormula);
        }
        SmartConLogger.Lookup($"  Pre-cached formulaByName: {formulaByName.Count} записей из {paramSnapshot.Count} параметров");

        var cm = instance.MEPModel?.ConnectorManager;
        var connector = cm?.FindByIndex(connectorIndex);
        if (connector is null)
        {
            SmartConLogger.Lookup($"  connector[{connectorIndex}]=null → return []");
            return [];
        }

        var targetOrigin = connector.CoordinateSystem.Origin;
        var instanceTransform = instance.GetTransform();

        var (directName, rootName, formula, _, isDiameter) =
            FamilyParameterAnalyzer.AnalyzeConnectorRadiusParam(
                familyDoc, instanceTransform, targetOrigin,
                instance.HandFlipped, instance.FacingFlipped);

        SmartConLogger.Lookup($"  FPA: directName='{directName}', rootName='{rootName}', formula='{formula}', isDiameter={isDiameter}");

        var searchParamName = rootName ?? directName;
        if (searchParamName is null)
        {
            SmartConLogger.Lookup("  searchParamName=null → return []");
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
                SmartConLogger.Lookup($"  tableStoresDiameters={tableStoresDiameters} (SolveFor rootRef={rootRef.Value:F3})");
            }
            else
            {
                // SolveFor не смог решить (size_lookup формула).
                // Если rootName — query-параметр size_lookup, то столбец хранит DN (диаметры).
                bool isQueryParam = false;
                try
                {
                    var sl = FormulaSolver.ParseSizeLookupStatic(formula);
                    isQueryParam = sl is not null && sl.Value.QueryParameters
                        .Any(q => string.Equals(q, rootName, StringComparison.OrdinalIgnoreCase));
                }
                catch { /* формула не парсится — оставляем false */ }

                tableStoresDiameters = isQueryParam || isDiameter;
                SmartConLogger.Lookup($"  tableStoresDiameters={tableStoresDiameters} (SolveFor=null, isQueryParam={isQueryParam}, isDiameter={isDiameter})");
            }
        }
        else
        {
            tableStoresDiameters = isDiameter;
            SmartConLogger.Lookup($"  tableStoresDiameters={tableStoresDiameters} (из FPA)");
        }

        var tableNames = fstm.GetAllSizeTableNames().ToList();
        var constrainedValues = new SortedSet<double>();
        var unconstrainedValues = new SortedSet<double>();

        SmartConLogger.Lookup($"  Таблицы: [{string.Join(", ", tableNames)}]");

        foreach (var tableName in tableNames)
        {
            var (colIndex, allQueryColumns, viaDependsOn) = FindColumnIndex(tableName, searchParamName, paramSnapshot, formulaByName);
            if (colIndex < 0)
            {
                SmartConLogger.Lookup($"  Таблица '{tableName}': colIndex не найден → пропуск");
                continue;
            }

            // Когда столбец найден через DependsOn (queryParam зависит от searchParam),
            // query column хранит производное значение (обычно DN = radius * 2).
            bool effectiveStoresDiam = tableStoresDiameters;
            if (viaDependsOn && !tableStoresDiameters)
            {
                effectiveStoresDiam = true;
                SmartConLogger.Lookup($"  DependsOn match: override tableStoresDiameters=true (column stores DN, not radius)");
            }

            bool isMultiColumn = allQueryColumns.Count > 1;
            SmartConLogger.Lookup($"  Таблица '{tableName}': colIndex={colIndex}, queryColumns={allQueryColumns.Count}, multiColumn={isMultiColumn}, storesDiam={effectiveStoresDiam}");

            var tempPath = Path.GetTempFileName();
            try
            {
                fstm.ExportSizeTable(tableName, tempPath);
                var lines = File.ReadAllLines(tempPath);
                var values = ExtractColumnValues(lines, colIndex, effectiveStoresDiam, allQueryColumns, constraints);
                SmartConLogger.Lookup($"  Экспортировано {values.Count} значений из таблицы '{tableName}'");

                var target = (isMultiColumn && constraints is { Count: > 0 })
                    ? constrainedValues
                    : unconstrainedValues;
                foreach (var v in values)
                    target.Add(v);
            }
            catch (Exception ex)
            {
                SmartConLogger.Lookup($"  ИСКЛЮЧЕНИЕ ExportSizeTable: {ex.Message}");
            }
            finally
            {
                try { File.Delete(tempPath); } catch { }
            }
        }

        // Если есть результаты из multi-column таблиц с constraints — используем только их,
        // чтобы single-column таблицы не «размывали» отфильтрованный набор
        var allValues = constrainedValues.Count > 0 ? constrainedValues : unconstrainedValues;
        if (constrainedValues.Count > 0 && unconstrainedValues.Count > 0)
            SmartConLogger.Lookup($"  [MultiCol] Приоритет multi-column: {constrainedValues.Count} constrained, {unconstrainedValues.Count} unconstrained → используем constrained");

        var result = new List<SizeOption>();
        foreach (var radius in allValues)
        {
            var dn = Math.Round(radius * 2.0 * 304.8);
            result.Add(new SizeOption
            {
                DisplayName = $"DN {dn}",
                Radius = radius,
                Source = "LookupTable"
            });
        }

        SmartConLogger.Lookup($"  Итого: {result.Count} уникальных размеров из LookupTable");
        return result;
    }

    // Маппинг query-столбца: (csvColIndex, parameterName)
    private readonly record struct QueryColumnEntry(int CsvColIndex, string ParameterName);

    /// <summary>
    /// Находит colIndex для searchParamName и возвращает все query-столбцы таблицы.
    /// colIndex = -1 если не найден.
    /// </summary>
    private static (int ColIndex, IReadOnlyList<QueryColumnEntry> AllQueryColumns, bool FoundViaDependsOn) FindColumnIndex(
        string tableName, string searchParamName,
        IReadOnlyList<(string? Name, string? Formula)> paramSnapshot,
        IReadOnlyDictionary<string, string> formulaByName)
    {
        SmartConLogger.Lookup($"    FindColumnIndex: table='{tableName}', searchParam='{searchParamName}'");

        int    targetCol       = -1;
        bool   foundViaDependsOn = false;
        IReadOnlyList<QueryColumnEntry>? allQueryColumns = null;

        foreach (var (fpName, fpFormula) in paramSnapshot)
        {
            if (string.IsNullOrEmpty(fpFormula)) continue;

            var parsed = FormulaSolver.ParseSizeLookupStatic(fpFormula);
            if (parsed is null) continue;

            var resolvedTableName = ResolveTableAlias(formulaByName, parsed.Value.TableName);
            if (!string.Equals(resolvedTableName, tableName, StringComparison.OrdinalIgnoreCase))
                continue;

            var queryParams = parsed.Value.QueryParameters;
            SmartConLogger.Lookup($"      FP='{fpName}': query=[{string.Join(", ", queryParams)}]");

            if (allQueryColumns is null)
                allQueryColumns = queryParams.Select((n, i) => new QueryColumnEntry(i + 1, n)).ToList();

            if (targetCol < 0)
            {
                for (int i = 0; i < queryParams.Count; i++)
                {
                    bool direct  = string.Equals(queryParams[i], searchParamName, StringComparison.OrdinalIgnoreCase);
                    bool depends = !direct && DependsOn(formulaByName, queryParams[i], searchParamName);
                    SmartConLogger.Lookup($"        [{i}] '{queryParams[i]}': direct={direct}, depends={depends}");
                    if (direct || depends)
                    {
                        targetCol = i + 1;
                        foundViaDependsOn = depends;
                        SmartConLogger.Lookup($"      → colIndex={targetCol}, viaDependsOn={depends}");
                        break;
                    }
                }
            }

            if (targetCol >= 0 && allQueryColumns is not null) break;
        }

        if (targetCol < 0)
            SmartConLogger.Lookup("      → colIndex=-1 (не найден)");

        return (targetCol, allQueryColumns ?? [], foundViaDependsOn);
    }

    private List<double> ExtractColumnValues(
        string[] csvLines,
        int colIndex,
        bool tableStoresDiameters,
        IReadOnlyList<QueryColumnEntry> allQueryColumns,
        IReadOnlyList<LookupColumnConstraint>? constraints)
    {
        var result = new List<double>();
        if (csvLines.Length == 0) return result;

        SmartConLogger.Lookup($"    [SizeResolver] ExtractColumnValues: colIndex={colIndex}, rows={csvLines.Length}, " +
            $"queryColumns={allQueryColumns.Count}, constraints={constraints?.Count ?? 0}");

        if (constraints is { Count: > 0 })
        {
            foreach (var c in constraints)
                SmartConLogger.Lookup($"      constraint: connIdx={c.ConnectorIndex}, param='{c.ParameterName}', value={c.ValueMm:F1}mm");
            SmartConLogger.Lookup($"      allQueryColumns: [{string.Join(", ", allQueryColumns.Select(q => $"col[{q.CsvColIndex}]='{q.ParameterName}'"))}]");
        }

        int parsed = 0, skipped = 0, filteredOut = 0;
        for (int row = 1; row < csvLines.Length; row++)
        {
            var line = csvLines[row];
            if (string.IsNullOrWhiteSpace(line)) { skipped++; continue; }

            var cols = line.Split(',');
            if (colIndex >= cols.Length) { skipped++; continue; }

            // Фильтрация по constraints (multi-column)
            if (constraints is { Count: > 0 } && allQueryColumns.Count > 1)
            {
                bool rowOk = true;
                foreach (var constraint in constraints)
                {
                    // Phase 1: сопоставление по имени параметра
                    var col = allQueryColumns.FirstOrDefault(q =>
                        string.Equals(q.ParameterName, constraint.ParameterName,
                            StringComparison.OrdinalIgnoreCase));

                    if (col.ParameterName is not null)
                    {
                        // Если constraint попал в целевой столбец — пропускаем (same-DN порт)
                        if (col.CsvColIndex == colIndex) continue;

                        // Имя совпало — проверяем значение
                        if (col.CsvColIndex >= cols.Length) { rowOk = false; break; }
                        var cv = cols[col.CsvColIndex].Trim().Trim('"');
                        if (!TryParseRevitValue(cv, out double cVal)) continue;
                        if (System.Math.Abs(cVal - constraint.ValueMm) > 0.02)
                        {
                            if (filteredOut < 5)
                                SmartConLogger.Lookup($"      row[{row}] FILTERED(name): col[{col.CsvColIndex}]='{cv}'={cVal:F1}mm ≠ {constraint.ValueMm:F1}mm");
                            rowOk = false;
                            break;
                        }
                    }
                    else
                    {
                        // Phase 2: имя не совпало — ищем по ЗНАЧЕНИЮ в query columns
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
                                    matchesTarget = true; // совпал с целевым столбцом — same-DN порт
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
                                SmartConLogger.Lookup($"      row[{row}] FILTERED(value): constraint DN={constraint.ValueMm:F0}mm не найден ни в одном query column");
                            rowOk = false;
                            break;
                        }
                        // found=true → constraint ограничивает non-target column
                        // matchesTarget && !found → constraint от same-DN порта, пропускаем (избыточный)
                    }
                }
                if (!rowOk) { filteredOut++; continue; }
            }

            var cell = cols[colIndex].Trim().Trim('"');
            if (TryParseRevitValue(cell, out double value))
            {
                double radius      = tableStoresDiameters ? value / 2.0 : value;
                double radiusInFeet = radius / 304.8;
                result.Add(radiusInFeet);
                parsed++;
            }
            else
            {
                skipped++;
            }
        }

        var distinct = result.Distinct().ToList();
        SmartConLogger.Lookup($"    [SizeResolver] → parsed={parsed}, skipped={skipped}, filteredOut={filteredOut}, unique={distinct.Count}");
        if (constraints is { Count: > 0 })
            SmartConLogger.Info($"[MultiCol] Dropdown фильтрация: parsed={parsed}, filteredOut={filteredOut}, unique={distinct.Count}");
        return result;
    }

    private static bool TryParseRevitValue(string cell, out double value)
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

        return double.TryParse(clean,
            NumberStyles.Float,
            CultureInfo.InvariantCulture, out value);
    }

    private static string ResolveTableAlias(IReadOnlyDictionary<string, string> formulaByName, string token)
    {
        if (token.StartsWith("\"") && token.EndsWith("\""))
            return token.Trim('"');
        if (formulaByName.TryGetValue(token, out var formula))
        {
            var f = formula.Trim();
            if (f.StartsWith("\"") && f.EndsWith("\""))
                return f.Trim('"');
        }
        return token;
    }

    private static bool DependsOn(IReadOnlyDictionary<string, string> formulaByName, string paramName, string target)
    {
        if (string.Equals(paramName, target, StringComparison.OrdinalIgnoreCase)) return true;
        if (!formulaByName.TryGetValue(paramName, out var formula)) return false;

        var vars = FormulaSolver.ExtractVariablesStatic(formula);
        if (vars.Count > 0)
            return vars.Any(v => string.Equals(v, target, StringComparison.OrdinalIgnoreCase));

        // ExtractVariablesStatic вернул [] — парсер не смог разобрать формулу
        // (имена переменных с пробелами, спецсимволы °, ³ и т.д.)
        // Fallback: проверяем содержит ли формула target как подстроку
        return formula.Contains(target, StringComparison.OrdinalIgnoreCase);
    }

    private List<SizeOption> GetFamilySymbolSizes(Document doc, FamilyInstance instance, int connectorIndex)
    {
        SmartConLogger.LookupSection("RevitDynamicSizeResolver.GetFamilySymbolSizes");

        var family = instance.Symbol?.Family;
        if (family is null)
        {
            SmartConLogger.Lookup("  family=null → return []");
            return [];
        }

        var symbolIds = family.GetFamilySymbolIds().ToList();
        SmartConLogger.Lookup($"  Семейство '{family.Name}': {symbolIds.Count} типоразмеров");

        var radii = new SortedSet<double>();

        // Обёртка в Transaction — SubTransaction требует активной Transaction
        // TODO: мигрировать на ITransactionService (I-03)
        using var tr = new Transaction(doc, "GetFamilySymbolSizes_Temp");
        try
        {
            tr.Start();
            foreach (var symbolId in symbolIds)
            {
                try
                {
                    var inst = doc.GetElement(instance.Id) as FamilyInstance;
                    if (inst is null) continue;

                    var sym = doc.GetElement(symbolId) as FamilySymbol;
                    inst.ChangeTypeId(symbolId);
                    doc.Regenerate();

                    var cm = inst.MEPModel?.ConnectorManager;
                    var conn = cm?.FindByIndex(connectorIndex);
                    if (conn is not null)
                    {
                        radii.Add(conn.Radius);
                        SmartConLogger.Lookup($"  symbol '{sym?.Name}': radius={conn.Radius * 304.8:F2} мм");
                    }
                }
                catch (Exception ex)
                {
                    SmartConLogger.Lookup($"  ИСКЛЮЧЕНИЕ symbolId={symbolId.Value}: {ex.Message}");
                }
            }
        }
        finally
        {
            if (tr.GetStatus() == TransactionStatus.Started)
                tr.RollBack();
        }

        var result = new List<SizeOption>();
        foreach (var radius in radii)
        {
            var dn = Math.Round(radius * 2.0 * 304.8);
            result.Add(new SizeOption
            {
                DisplayName = $"DN {dn}",
                Radius = radius,
                Source = "FamilySymbol"
            });
        }

        SmartConLogger.Lookup($"  Итого: {result.Count} уникальных размеров из FamilySymbol");
        return result;
    }

    // ── GetAvailableFamilySizes ──────────────────────────────────────────────

    public IReadOnlyList<FamilySizeOption> GetAvailableFamilySizes(Document doc, ElementId elementId,
        int targetConnectorIndex)
    {
        SmartConLogger.LookupSection("RevitDynamicSizeResolver.GetAvailableFamilySizes");
        SmartConLogger.Lookup($"  elementId={elementId.Value}, targetConnIdx={targetConnectorIndex}");

        var element = doc.GetElement(elementId);
        if (element is null)
            return [];

        if (element is MEPCurve or FlexPipe)
        {
            var pipeSizes = GetPipeSizes(doc, element);
            var currentRadius = pipeSizes.Count > 0 ? pipeSizes[0].Radius : 0;
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
            SmartConLogger.Lookup($"  LookupTable: {lookupRows.Count} конфигураций");
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
            SmartConLogger.Lookup($"  FamilySymbol fallback: {symbolConfigs.Count} конфигураций");
            options.AddRange(symbolConfigs);
        }

        var deduped = DeduplicateFamilyOptions(options);
        var sorted = SortByTargetDn(deduped);
        SmartConLogger.Lookup($"  → {sorted.Count} уникальных конфигураций (отсортировано)");
        return sorted;
    }

    private List<FamilySizeOption> GetFamilySymbolConfigurations(Document doc, FamilyInstance instance, int targetConnectorIndex)
    {
        var family = instance.Symbol?.Family;
        if (family is null) return [];

        var symbolIds = family.GetFamilySymbolIds().ToList();
        var currentSymbolId = instance.Symbol?.Id;
        SmartConLogger.Lookup($"  FamilySymbol configs: '{family.Name}', {symbolIds.Count} symbols, current={currentSymbolId?.Value}");

        var configs = new List<FamilySizeOption>();
        var symbolData = new List<(ElementId SymbolId, Dictionary<int, double> ConnectorRadii, string SymbolName)>();

        using var tr = new Transaction(doc, "GetFamilySymbolConfigs_Temp");
        try
        {
            tr.Start();

            foreach (var symbolId in symbolIds)
            {
                try
                {
                    var inst = doc.GetElement(instance.Id) as FamilyInstance;
                    if (inst is null) continue;

                    var sym = doc.GetElement(symbolId) as FamilySymbol;
                    inst.ChangeTypeId(symbolId);
                    doc.Regenerate();

                    var cm = inst.MEPModel?.ConnectorManager;
                    if (cm is null) continue;

                    var connectorRadii = new Dictionary<int, double>();
                    foreach (Connector c in cm.Connectors)
                    {
                        if (c.ConnectorType == ConnectorType.Curve) continue;
                        connectorRadii[(int)c.Id] = c.Radius;
                    }

                    var targetRadius = connectorRadii.GetValueOrDefault(targetConnectorIndex, 0);
                    if (targetRadius <= 0) continue;

                    symbolData.Add((symbolId, connectorRadii, sym?.Name ?? ""));
                }
                catch (Exception ex)
                {
                    SmartConLogger.Lookup($"  error symbolId={symbolId.Value}: {ex.Message}");
                }
            }

            var sharedParamGroups = AnalyzeSharedParameterGroups(symbolData);

            foreach (var (symbolId, connectorRadii, symbolName) in symbolData)
            {
                var displayRadii = BuildDisplayRadii(connectorRadii, sharedParamGroups, targetConnectorIndex);
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
        }
        finally
        {
            if (tr.GetStatus() == TransactionStatus.Started)
                tr.RollBack();
        }

        return configs;
    }

    private static List<List<int>> AnalyzeSharedParameterGroups(
        List<(ElementId SymbolId, Dictionary<int, double> ConnectorRadii, string SymbolName)> symbolData)
    {
        if (symbolData.Count == 0) return [];

        var allIds = symbolData[0].ConnectorRadii.Keys.OrderBy(id => id).ToList();
        var groups = new List<List<int>>();
        var assigned = new HashSet<int>();

        foreach (var id in allIds)
        {
            if (assigned.Contains(id)) continue;

            var group = new List<int> { id };
            assigned.Add(id);

            foreach (var otherId in allIds)
            {
                if (assigned.Contains(otherId)) continue;

                bool alwaysSame = symbolData.All(sd =>
                {
                    var r1 = sd.ConnectorRadii.GetValueOrDefault(id, -1);
                    var r2 = sd.ConnectorRadii.GetValueOrDefault(otherId, -1);
                    return System.Math.Abs(r1 - r2) < 1e-9;
                });

                if (alwaysSame)
                {
                    group.Add(otherId);
                    assigned.Add(otherId);
                }
            }

            groups.Add(group);
        }

        return groups;
    }

    private static Dictionary<int, double> BuildDisplayRadii(
        Dictionary<int, double> connectorRadii,
        List<List<int>> sharedParamGroups,
        int targetConnectorIndex)
    {
        var result = new Dictionary<int, double>();

        foreach (var group in sharedParamGroups)
        {
            var repId = group.Contains(targetConnectorIndex)
                ? targetConnectorIndex
                : group[0];

            if (connectorRadii.TryGetValue(repId, out var radius))
                result[repId] = radius;
        }

        return result;
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
