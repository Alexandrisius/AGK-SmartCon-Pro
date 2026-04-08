using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Math;
using SmartCon.Core.Math.FormulaEngine.Solver;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Extensions;
using RevitFamily = Autodesk.Revit.DB.Family;
using RevitTransform = Autodesk.Revit.DB.Transform;

namespace SmartCon.Revit.Parameters;

/// <summary>
/// Реализация ILookupTableService через FamilySizeTableManager.
/// Вызывать ВНЕ транзакции: EditFamily требует doc.IsModifiable == false.
/// Анализирует формулы size_lookup(...) чтобы найти нужный столбец CSV.
/// </summary>
public sealed class RevitLookupTableService : ILookupTableService
{
    // ── ILookupTableService ───────────────────────────────────────────────

    public bool ConnectorRadiusExistsInTable(Document doc, ElementId elementId,
        int connectorIndex, double radiusInternalUnits,
        IReadOnlyList<LookupColumnConstraint>? constraints = null)
    {
        SmartConLogger.LookupSection("ConnectorRadiusExistsInTable");
        SmartConLogger.Lookup($"  elementId={elementId.Value}, connIdx={connectorIndex}, radiusInternal={radiusInternalUnits:F6} ft ({radiusInternalUnits * 304.8:F2} mm)");

        var ctx = BuildLookupContext(doc, elementId, connectorIndex);
        if (ctx is null)
        {
            SmartConLogger.Lookup("  → ctx=null, нет таблицы → return false");
            return false;
        }

        var values = ExtractColumnValues(ctx.CsvLines, ctx.ColIndex, ctx.AllQueryColumns, constraints);
        double targetMm = ToMillimeters(radiusInternalUnits, ctx.IsRadius);

        SmartConLogger.Lookup($"  targetMm={targetMm:F3} (isRadius={ctx.IsRadius}), значений в колонке: {values.Count}");
        SmartConLogger.Lookup($"  Значения в таблице (мм): [{string.Join(", ", values.Select(v => $"{v:F2}"))}]");

        bool found = values.Any(v => System.Math.Abs(v - targetMm) < 0.02);
        SmartConLogger.Lookup($"  → ExistsInTable={found} (допуск 0.02 мм)");
        return found;
    }

    public double GetNearestAvailableRadius(Document doc, ElementId elementId,
        int connectorIndex, double targetRadiusInternalUnits,
        IReadOnlyList<LookupColumnConstraint>? constraints = null)
    {
        SmartConLogger.LookupSection("GetNearestAvailableRadius");
        SmartConLogger.Lookup($"  elementId={elementId.Value}, connIdx={connectorIndex}, targetInternal={targetRadiusInternalUnits:F6} ft");

        var ctx = BuildLookupContext(doc, elementId, connectorIndex);
        if (ctx is null)
        {
            SmartConLogger.Lookup("  → ctx=null, нет таблицы → вернуть target как есть");
            return targetRadiusInternalUnits;
        }

        var values = ExtractColumnValues(ctx.CsvLines, ctx.ColIndex, ctx.AllQueryColumns, constraints);
        if (values.Count == 0)
        {
            SmartConLogger.Lookup("  → колонка пуста → вернуть target как есть");
            return targetRadiusInternalUnits;
        }

        double targetMm = ToMillimeters(targetRadiusInternalUnits, ctx.IsRadius);
        SmartConLogger.Lookup($"  targetMm={targetMm:F3}, значений в колонке: {values.Count}");
        SmartConLogger.Lookup($"  Значения (мм): [{string.Join(", ", values.Select(v => $"{v:F2}"))}]");

        double nearestMm  = values[0];
        double nearestDiff = System.Math.Abs(values[0] - targetMm);

        foreach (var v in values.Skip(1))
        {
            var diff = System.Math.Abs(v - targetMm);
            if (diff < nearestDiff)
            {
                nearestDiff = diff;
                nearestMm   = v;
            }
        }

        double result = FromMillimeters(nearestMm, ctx.IsRadius);
        SmartConLogger.Lookup($"  → ближайший={nearestMm:F2} мм (delta={nearestDiff:F3} мм), internal={result:F6} ft");
        return result;
    }

    public bool HasLookupTable(Document doc, ElementId elementId, int connectorIndex)
    {
        SmartConLogger.LookupSection("HasLookupTable");
        SmartConLogger.Lookup($"  elementId={elementId.Value}, connIdx={connectorIndex}");
        bool has = BuildLookupContext(doc, elementId, connectorIndex) is not null;
        SmartConLogger.Lookup($"  → HasLookupTable={has}");
        return has;
    }

    // ── Построение контекста ──────────────────────────────────────────────

    /// <summary>
    /// Маппинг одного query-столбца CSV: индекс столбца и имя FamilyParameter.
    /// Нужен для фильтрации строк по ограничениям других коннекторов.
    /// </summary>
    private sealed record QueryColumnMapping(int CsvColIndex, string ParameterName);

    private sealed record LookupContext(
        string[] CsvLines,
        int ColIndex,
        bool IsRadius,
        IReadOnlyList<QueryColumnMapping> AllQueryColumns);   // ВСЕ query-столбцы таблицы

    /// <summary>
    /// Открывает семейство, ищет FamilySizeTableManager, находит таблицу и столбец
    /// управляющий радиусом нужного коннектора. Экспортирует CSV в temp-файл.
    /// Возвращает null если таблица не найдена.
    /// </summary>
    private LookupContext? BuildLookupContext(Document doc, ElementId elementId, int connectorIndex)
    {
        SmartConLogger.LookupSection("BuildLookupContext");
        var element = doc.GetElement(elementId);
        if (element is null)
        {
            SmartConLogger.Lookup("  element=null → return null");
            return null;
        }

        SmartConLogger.Lookup($"  element={element.Name} ({element.GetType().Name}), id={elementId.Value}");

        // MEP Curve не имеет LookupTable
        if (element is MEPCurve or FlexPipe)
        {
            SmartConLogger.Lookup("  element is MEPCurve/FlexPipe → нет LookupTable → return null");
            return null;
        }

        if (element is not FamilyInstance instance)
        {
            SmartConLogger.Lookup($"  element is not FamilyInstance ({element.GetType().Name}) → return null");
            return null;
        }

        RevitFamily? family = instance.Symbol?.Family;
        if (family is null)
        {
            SmartConLogger.Lookup("  family=null (Symbol или Family не найдено) → return null");
            return null;
        }

        SmartConLogger.Lookup($"  family='{family.Name}', symbol='{instance.Symbol?.Name}'");

        if (doc.IsModifiable)
        {
            SmartConLogger.Warn("[LookupSvc] BuildLookupContext вызван внутри транзакции — запрещено!");
            return null;
        }

        // Получить origin коннектора для FamilyParameterAnalyzer
        var cm        = instance.MEPModel?.ConnectorManager;
        var connector = cm?.FindByIndex(connectorIndex);
        if (connector is null)
        {
            SmartConLogger.Lookup($"  connector[{connectorIndex}]=null (ConnectorManager: {(cm is null ? "null" : $"{cm.Connectors.Size} conn")}) → return null");
            return null;
        }

        var targetOriginGlobal = connector.CoordinateSystem.Origin;
        var instanceTransform  = instance.GetTransform();
        SmartConLogger.Lookup($"  connector[{connectorIndex}] origin=({targetOriginGlobal.X:F4}, {targetOriginGlobal.Y:F4}, {targetOriginGlobal.Z:F4})");

        Document? familyDoc = null;
        try
        {
            SmartConLogger.Lookup($"  → EditFamily('{family.Name}')...");
            familyDoc = doc.EditFamily(family);
            SmartConLogger.Lookup($"  → familyDoc открыт: '{familyDoc?.Title}'");
            return BuildLookupContextFromFamily(familyDoc!, instanceTransform, targetOriginGlobal,
                instance.HandFlipped, instance.FacingFlipped);
        }
        catch (Exception ex)
        {
            SmartConLogger.Lookup($"  ИСКЛЮЧЕНИЕ EditFamily: {ex.GetType().Name}: {ex.Message}");
            SmartConLogger.Error($"[LookupSvc] BuildLookupContext EditFamily failed: {ex}");
            return null;
        }
        finally
        {
            if (familyDoc is not null)
            {
                familyDoc.Close(false);
                SmartConLogger.Lookup("  → familyDoc закрыт");
            }
        }
    }

    private static LookupContext? BuildLookupContextFromFamily(
        Document familyDoc,
        RevitTransform instanceTransform,
        XYZ targetOriginGlobal,
        bool handFlipped = false,
        bool facingFlipped = false)
    {
        SmartConLogger.LookupSection("BuildLookupContextFromFamily");

        // Получить FamilySizeTableManager
        var fstm = FamilySizeTableManager.GetFamilySizeTableManager(
            familyDoc, familyDoc.OwnerFamily.Id);

        if (fstm is null)
        {
            SmartConLogger.Lookup("  FamilySizeTableManager=null → нет таблиц в семействе");
            return null;
        }

        SmartConLogger.Lookup($"  FamilySizeTableManager: NumberOfSizeTables={fstm.NumberOfSizeTables}");

        if (fstm.NumberOfSizeTables == 0)
        {
            SmartConLogger.Lookup("  NumberOfSizeTables=0 → нет таблиц → return null");
            return null;
        }

        var tableNames = fstm.GetAllSizeTableNames().ToList();
        SmartConLogger.Lookup($"  Таблицы ({tableNames.Count}): [{string.Join(", ", tableNames)}]");

        // PRE-CACHE: материализуем fm.Parameters ДО вызова FPA,
        // чтобы избежать COM-коррупции ParameterSet после AssociatedParameters доступа
        var fm = familyDoc.FamilyManager;
        var paramSnapshot = new List<(string? Name, string? Formula)>();
        var formulaByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (FamilyParameter fp in fm.Parameters)
        {
            var name = fp.Definition?.Name;
            var formula_ = fp.Formula;
            paramSnapshot.Add((name, formula_));
            if (name != null && !string.IsNullOrEmpty(formula_))
                formulaByName.TryAdd(name, formula_);
        }
        SmartConLogger.Lookup($"  Pre-cached formulaByName: {formulaByName.Count} записей из {paramSnapshot.Count} параметров");

        // Анализ параметра коннектора
        SmartConLogger.Lookup("  → FamilyParameterAnalyzer.AnalyzeConnectorRadiusParam...");
        var (directName, rootName, formula, isInstance, isDiameter) =
            FamilyParameterAnalyzer.AnalyzeConnectorRadiusParam(
                familyDoc, instanceTransform, targetOriginGlobal,
                handFlipped, facingFlipped);

        SmartConLogger.Lookup($"  FPA результат: directName='{directName}', rootName='{rootName}', formula='{formula}', isInstance={isInstance}, isDiameter={isDiameter}");

        // Параметр поиска — корневой (если есть формула) или прямой
        var searchParamName = rootName ?? directName;
        if (searchParamName is null)
        {
            SmartConLogger.Lookup("  searchParamName=null (FPA не нашёл параметр) → return null");
            return null;
        }

        SmartConLogger.Lookup($"  searchParamName='{searchParamName}' (используем для поиска в таблице)");

        // Определяем: значения в таблице — радиусы или диаметры?
        // Если есть формула + rootParam (например 'Условный радиус = ADSK_Диаметр условный / 2'),
        // то searchParam — это rootParam, и его тип (diameter/radius) нужно вычислить через SolveFor:
        //   SolveFor(formula, rootParam, radius=1.0) → если rootRef ≈ 2.0 → rootParam = diameter.
        bool tableStoresDiameters;
        if (rootName is not null && formula is not null)
        {
            double refRadius  = 1.0;
            double directRef  = isDiameter ? refRadius * 2.0 : refRadius;
            var    rootRef    = FormulaSolver.SolveForStatic(formula, rootName, directRef);
            if (rootRef.HasValue)
            {
                tableStoresDiameters = System.Math.Abs(rootRef.Value / refRadius - 2.0) < 0.1;
                SmartConLogger.Lookup($"  tableStoresDiameters={tableStoresDiameters} (SolveFor rootRef={rootRef.Value:F3}, ratio={rootRef.Value / refRadius:F3})");
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
            SmartConLogger.Lookup($"  tableStoresDiameters={tableStoresDiameters} (isDiameter из FPA, isRadius=!{tableStoresDiameters})");
        }

        // Перебрать все таблицы и найти ту, где searchParamName участвует как queryParam
        foreach (var tableName in tableNames)
        {
            SmartConLogger.Lookup($"  → TryGetContextForTable('{tableName}', searchParam='{searchParamName}')...");
            var ctx = TryGetContextForTable(fstm, tableName, searchParamName, tableStoresDiameters, paramSnapshot, formulaByName);
            if (ctx is not null)
            {
                SmartConLogger.Lookup($"  ✓ Контекст найден: tableName='{tableName}', colIndex={ctx.ColIndex}, isRadius={ctx.IsRadius}, строк CSV={ctx.CsvLines.Length}");
                return ctx;
            }
        }

        SmartConLogger.Lookup($"  ✗ Ни одна таблица не содержит параметр '{searchParamName}' как queryParam → return null");
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
        int    targetColIndex = -1;
        string? foundInParam  = null;
        bool   foundViaDependsOn = false;
        IReadOnlyList<QueryColumnMapping>? allQueryColumns = null;

        SmartConLogger.Lookup($"    Перебор параметров семейства (кол-во: {paramSnapshot.Count}):");

        foreach (var (fpName, fpFormula) in paramSnapshot)
        {
            if (string.IsNullOrEmpty(fpFormula)) continue;

            var parsed = FormulaSolver.ParseSizeLookupStatic(fpFormula);
            if (parsed is null) continue;

            var resolvedTableName = ResolveTableAlias(formulaByName, parsed.Value.TableName);
            if (!string.Equals(resolvedTableName, tableName, StringComparison.OrdinalIgnoreCase))
                continue;

            var queryParams = parsed.Value.QueryParameters;
            SmartConLogger.Lookup($"      FamilyParam '{fpName}': queryParams=[{string.Join(", ", queryParams)}]");

            // При первом совпадении таблицы строим маппинг ВСЕХ query-столбцов
            // col[0]=комментарии, col[1]=queryParams[0], col[2]=queryParams[1], ...
            if (allQueryColumns is null)
            {
                allQueryColumns = queryParams
                    .Select((name, idx) => new QueryColumnMapping(idx + 1, name))
                    .ToList();
            }

            // Найти позицию searchParamName
            if (targetColIndex < 0)
            {
                for (int i = 0; i < queryParams.Count; i++)
                {
                    bool direct  = string.Equals(queryParams[i], searchParamName, StringComparison.OrdinalIgnoreCase);
                    bool depends = !direct && DependsOn(formulaByName, queryParams[i], searchParamName);
                    SmartConLogger.Lookup($"        [{i}] '{queryParams[i]}': direct={direct}, depends={depends}");
                    if (direct || depends)
                    {
                        targetColIndex    = i + 1;
                        foundInParam      = fpName;
                        foundViaDependsOn = depends;
                        SmartConLogger.Lookup($"        → searchParam '{searchParamName}' @ queryIdx={i}, colIndex={targetColIndex}, viaDependsOn={depends}");
                        break;
                    }
                }
            }

            if (targetColIndex >= 0 && allQueryColumns is not null) break;
        }

        if (targetColIndex < 0)
        {
            SmartConLogger.Lookup($"    ✗ Таблица '{tableName}': параметр '{searchParamName}' не найден");
            return null;
        }

        SmartConLogger.Lookup($"    ✓ Таблица '{tableName}': colIndex={targetColIndex}, найдено в '{foundInParam}'");
        SmartConLogger.Lookup($"      AllQueryColumns: [{string.Join(", ", (allQueryColumns ?? []).Select(q => $"col[{q.CsvColIndex}]={q.ParameterName}"))}]");

        var tempPath = Path.GetTempFileName();
        try
        {
            SmartConLogger.Lookup($"    → ExportSizeTable('{tableName}') в '{tempPath}'...");
            fstm.ExportSizeTable(tableName, tempPath);
            var lines = File.ReadAllLines(tempPath);
            SmartConLogger.Lookup($"    → Экспортировано {lines.Length} строк");
            SmartConLogger.LookupLines($"    CSV таблицы '{tableName}'", lines, 30);
            // Когда столбец найден через DependsOn (queryParam зависит от searchParam),
            // query column хранит производное значение (обычно DN = radius * 2).
            // Переопределяем tableStoresDiameters = true.
            bool effectiveStoresDiam = tableStoresDiameters;
            if (foundViaDependsOn && !tableStoresDiameters)
            {
                effectiveStoresDiam = true;
                SmartConLogger.Lookup($"    → DependsOn match: override tableStoresDiameters=true (column stores DN, not radius)");
            }
            return new LookupContext(lines, targetColIndex, !effectiveStoresDiam, allQueryColumns ?? []);
        }
        catch (Exception ex)
        {
            SmartConLogger.Lookup($"    ИСКЛЮЧЕНИЕ ExportSizeTable: {ex.GetType().Name}: {ex.Message}");
            SmartConLogger.Error($"[LookupSvc] ExportSizeTable failed: {ex}");
            return null;
        }
        finally
        {
            try { File.Delete(tempPath); } catch { /* ignored */ }
        }
    }

    // ── Парсинг CSV ───────────────────────────────────────────────────────

    /// <summary>
    /// Извлечь уникальные числовые значения из указанного столбца CSV.
    /// Первая строка CSV — заголовок (пропускается).
    /// constraints — если заданы, пропускать строки где другие query-столбцы не совпадают.
    /// </summary>
    private static List<double> ExtractColumnValues(
        string[] csvLines,
        int colIndex,
        IReadOnlyList<QueryColumnMapping> allQueryColumns,
        IReadOnlyList<LookupColumnConstraint>? constraints)
    {
        var result = new List<double>();

        SmartConLogger.Lookup($"    [LookupSvc] ExtractColumnValues: colIndex={colIndex}, строк={csvLines.Length}, " +
            $"queryColumns={allQueryColumns.Count}, constraints={constraints?.Count ?? 0}");

        if (constraints is { Count: > 0 })
        {
            foreach (var c in constraints)
                SmartConLogger.Lookup($"      constraint: connIdx={c.ConnectorIndex}, param='{c.ParameterName}', value={c.ValueMm:F1}mm");
            SmartConLogger.Lookup($"      allQueryColumns: [{string.Join(", ", allQueryColumns.Select(q => $"col[{q.CsvColIndex}]='{q.ParameterName}'"))}]");
        }

        if (csvLines.Length == 0)
        {
            SmartConLogger.Lookup("    CSV пустой!");
            return result;
        }

        SmartConLogger.Lookup($"    Заголовок CSV (строка 0): '{csvLines[0]}'");

        int parsed = 0, skipped = 0, filteredOut = 0;
        for (int row = 1; row < csvLines.Length; row++)
        {
            var line = csvLines[row];
            if (string.IsNullOrWhiteSpace(line)) { skipped++; continue; }

            var cols = SplitCsvLine(line);

            if (colIndex >= cols.Length)
            {
                skipped++;
                continue;
            }

            // Фильтрация по constraints (multi-column): проверяем значения других query-столбцов
            if (constraints is { Count: > 0 } && allQueryColumns.Count > 1)
            {
                bool rowOk = true;
                foreach (var constraint in constraints)
                {
                    // Phase 1: сопоставление по имени параметра
                    var colMap = allQueryColumns.FirstOrDefault(q =>
                        string.Equals(q.ParameterName, constraint.ParameterName,
                            StringComparison.OrdinalIgnoreCase));

                    if (colMap is not null)
                    {
                        // Если constraint попал в целевой столбец — пропускаем (same-DN порт)
                        if (colMap.CsvColIndex == colIndex) continue;

                        // Имя совпало — проверяем значение
                        if (colMap.CsvColIndex >= cols.Length) { rowOk = false; break; }

                        var constraintCell = cols[colMap.CsvColIndex].Trim().Trim('"');
                        if (!TryParseRevitValue(constraintCell, out double constraintVal))
                            continue;

                        if (System.Math.Abs(constraintVal - constraint.ValueMm) > 0.02)
                        {
                            if (filteredOut < 3)
                                SmartConLogger.Lookup($"      row[{row}] FILTERED(name): col[{colMap.CsvColIndex}]='{constraintCell}'={constraintVal:F1}mm ≠ {constraint.ValueMm:F1}mm");
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
                            if (filteredOut < 3)
                                SmartConLogger.Lookup($"      row[{row}] FILTERED(value): constraint DN={constraint.ValueMm:F0}mm не найден ни в одном query column");
                            rowOk = false;
                            break;
                        }
                    }
                }

                if (!rowOk)
                {
                    filteredOut++;
                    continue;
                }
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

        var distinct = result.Distinct().OrderBy(v => v).ToList();
        SmartConLogger.Lookup($"    → Итого: распарсено={parsed}, пропущено={skipped}, отфильтровано={filteredOut}, уникальных={distinct.Count}");
        SmartConLogger.Lookup($"    → Уникальные значения (мм): [{string.Join(", ", distinct.Select(v => $"{v:F2}"))}]");
        if (constraints is { Count: > 0 })
            SmartConLogger.Info($"[MultiCol] S4 LookupTable фильтрация: parsed={parsed}, filteredOut={filteredOut}, unique={distinct.Count}");
        return distinct;
    }

    /// <summary>
    /// Разбить строку CSV с учётом кавычек.
    /// </summary>
    private static string[] SplitCsvLine(string line)
        => line.Split(',');

    /// <summary>
    /// Парсит строку вида "50 mm", "0.05", "50.0" → числовое значение (мм).
    /// </summary>
    private static bool TryParseRevitValue(string cell, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(cell)) return false;

        // Убрать единицы измерения (mm, m, ft, in, ") и пробелы
        var clean = cell
            .Replace("\"", "")
            .Replace(" mm", "").Replace("mm", "")
            .Replace(" m",  "").Replace("m",  "")
            .Replace(" ft", "").Replace("ft", "")
            .Replace(" in", "").Replace("in", "")
            .Trim();

        return double.TryParse(clean,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    // ── Конвертации единиц ────────────────────────────────────────────────

    /// <summary>
    /// Конвертировать internal units (feet) в миллиметры.
    /// isRadius=true → radii (feet), isRadius=false → diameter в мм (feet * 304.8 * 2 → нет, мм всегда мм).
    /// Таблицы Revit хранят в мм.
    /// </summary>
    private static double ToMillimeters(double internalUnits, bool isRadius)
    {
        // 1 foot = 304.8 мм
        double mm = internalUnits * 304.8;
        // Если таблица хранит диаметры, а у нас радиус → умножить на 2
        return isRadius ? mm : mm * 2.0;
    }

    private static double FromMillimeters(double mm, bool isRadius)
    {
        double feet = mm / 304.8;
        return isRadius ? feet : feet / 2.0;
    }

    // ── Вспомогательные для TryGetContextForTable ─────────────────────────

    /// <summary>
    /// Если token — имя FamilyParameter со строковой формулой вида "SomeName",
    /// возвращает это строковое значение. Иначе возвращает token как есть.
    /// Нужно чтобы резолвить size_lookup(BP_LookupTable, ...) где BP_LookupTable
    /// — параметр с formula='"BP_A0206_Giacomini_R910_ВР-ВР"'.
    /// </summary>
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

    /// <summary>
    /// Возвращает true если paramName == target или FamilyParameter paramName
    /// имеет формулу содержащую target как переменную (1 уровень зависимости).
    /// Нужно чтобы найти BP_NominalDiameter как queryParam когда searchParam='DN'
    /// и BP_NominalDiameter имеет формулу 'DN / 2 * 2 + ...'.
    /// </summary>
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
}
