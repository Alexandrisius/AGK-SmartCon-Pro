using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Math;
using SmartCon.Core.Math.FormulaEngine.Solver;
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
        int connectorIndex, double radiusInternalUnits)
    {
        SmartConLogger.LookupSection("ConnectorRadiusExistsInTable");
        SmartConLogger.Lookup($"  elementId={elementId.Value}, connIdx={connectorIndex}, radiusInternal={radiusInternalUnits:F6} ft ({radiusInternalUnits * 304.8:F2} mm)");

        var ctx = BuildLookupContext(doc, elementId, connectorIndex);
        if (ctx is null)
        {
            SmartConLogger.Lookup("  → ctx=null, нет таблицы → return false");
            return false;
        }

        var values = ExtractColumnValues(ctx.CsvLines, ctx.ColIndex);
        double targetMm = ToMillimeters(radiusInternalUnits, ctx.IsRadius);

        SmartConLogger.Lookup($"  targetMm={targetMm:F3} (isRadius={ctx.IsRadius}), значений в колонке: {values.Count}");
        SmartConLogger.Lookup($"  Значения в таблице (мм): [{string.Join(", ", values.Select(v => $"{v:F2}"))}]");

        bool found = values.Any(v => System.Math.Abs(v - targetMm) < 0.02);
        SmartConLogger.Lookup($"  → ExistsInTable={found} (допуск 0.02 мм)");
        return found;
    }

    public double GetNearestAvailableRadius(Document doc, ElementId elementId,
        int connectorIndex, double targetRadiusInternalUnits)
    {
        SmartConLogger.LookupSection("GetNearestAvailableRadius");
        SmartConLogger.Lookup($"  elementId={elementId.Value}, connIdx={connectorIndex}, targetInternal={targetRadiusInternalUnits:F6} ft");

        var ctx = BuildLookupContext(doc, elementId, connectorIndex);
        if (ctx is null)
        {
            SmartConLogger.Lookup("  → ctx=null, нет таблицы → вернуть target как есть");
            return targetRadiusInternalUnits;
        }

        var values = ExtractColumnValues(ctx.CsvLines, ctx.ColIndex);
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

    private sealed record LookupContext(
        string[] CsvLines,
        int ColIndex,
        bool IsRadius);   // true = значения в мм уже радиусы, false = диаметры (делить на 2)

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
            return BuildLookupContextFromFamily(familyDoc!, instanceTransform, targetOriginGlobal);
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
        XYZ targetOriginGlobal)
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

        // Анализ параметра коннектора
        SmartConLogger.Lookup("  → FamilyParameterAnalyzer.AnalyzeConnectorRadiusParam...");
        var (directName, rootName, formula, isInstance, isDiameter) =
            FamilyParameterAnalyzer.AnalyzeConnectorRadiusParam(
                familyDoc, instanceTransform, targetOriginGlobal);

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
                tableStoresDiameters = isDiameter;
                SmartConLogger.Lookup($"  tableStoresDiameters={tableStoresDiameters} (SolveFor=null, fallback isDiameter)");
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
            var ctx = TryGetContextForTable(familyDoc, fstm, tableName, searchParamName, tableStoresDiameters);
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
        Document familyDoc,
        FamilySizeTableManager fstm,
        string tableName,
        string searchParamName,
        bool tableStoresDiameters)
    {
        // Проверить: есть ли формула size_lookup(tableName, ..., searchParamName, ...) у любого параметра
        var fm = familyDoc.FamilyManager;
        int colIndex = -1;
        string? foundInParam = null;
        string? foundFormula = null;

        SmartConLogger.Lookup($"    Перебор параметров семейства (кол-во: {fm.Parameters.Size}):");

        foreach (FamilyParameter fp in fm.Parameters)
        {
            if (string.IsNullOrEmpty(fp.Formula)) continue;

            SmartConLogger.Lookup($"      FamilyParam '{fp.Definition?.Name}', formula='{fp.Formula}'");

            var parsed = FormulaSolver.ParseSizeLookupStatic(fp.Formula);
            if (parsed is null)
            {
                SmartConLogger.Lookup($"        → ParseSizeLookup=null (не size_lookup)");
                continue;
            }

            var resolvedTableName = ResolveTableAlias(fm, parsed.Value.TableName);
            SmartConLogger.Lookup($"        → ParseSizeLookup OK: tableName='{parsed.Value.TableName}'→'{resolvedTableName}', target='{parsed.Value.TargetParameter}', queryParams=[{string.Join(", ", parsed.Value.QueryParameters)}]");

            if (!string.Equals(resolvedTableName, tableName, StringComparison.OrdinalIgnoreCase))
            {
                SmartConLogger.Lookup($"        → tableName не совпадает ('{resolvedTableName}' != '{tableName}') — пропускаем");
                continue;
            }

            // Найти позицию searchParamName среди QueryParameters
            var queryParams = parsed.Value.QueryParameters;
            SmartConLogger.Lookup($"        → Ищем '{searchParamName}' среди queryParams: [{string.Join(", ", queryParams)}]");

            for (int i = 0; i < queryParams.Count; i++)
            {
                if (string.Equals(queryParams[i], searchParamName, StringComparison.OrdinalIgnoreCase)
                    || DependsOn(fm, queryParams[i], searchParamName))
                {
                    // colIndex в CSV: col[0] = комментарии, col[queryParamIndex+1] = значения param
                    colIndex     = i + 1;
                    foundInParam = fp.Definition?.Name;
                    foundFormula = fp.Formula;
                    SmartConLogger.Lookup($"        → НАЙДЕНО! queryParamIndex={i}, colIndex={colIndex} (col[0]=комментарии)");
                    break;
                }
            }

            if (colIndex >= 0) break;

            SmartConLogger.Lookup($"        → '{searchParamName}' не найден в queryParams этой формулы");
        }

        if (colIndex < 0)
        {
            SmartConLogger.Lookup($"    ✗ Таблица '{tableName}': параметр '{searchParamName}' не найден ни в одной формуле size_lookup");
            return null;
        }

        SmartConLogger.Lookup($"    ✓ Таблица '{tableName}': colIndex={colIndex}, найдено в параметре '{foundInParam}' (formula='{foundFormula}')");

        // Экспортировать таблицу в temp CSV и прочитать
        var tempPath = Path.GetTempFileName();
        try
        {
            SmartConLogger.Lookup($"    → ExportSizeTable('{tableName}') в '{tempPath}'...");
            fstm.ExportSizeTable(tableName, tempPath);
            var lines = File.ReadAllLines(tempPath);
            SmartConLogger.Lookup($"    → Экспортировано {lines.Length} строк");
            SmartConLogger.LookupLines($"    CSV таблицы '{tableName}'", lines, 30);
            return new LookupContext(lines, colIndex, !tableStoresDiameters);
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
    /// </summary>
    private static List<double> ExtractColumnValues(string[] csvLines, int colIndex)
    {
        var result = new List<double>();

        SmartConLogger.Lookup($"    ExtractColumnValues: colIndex={colIndex}, строк={csvLines.Length}");

        if (csvLines.Length == 0)
        {
            SmartConLogger.Lookup("    CSV пустой!");
            return result;
        }

        SmartConLogger.Lookup($"    Заголовок CSV (строка 0): '{csvLines[0]}'");

        // Пропускаем заголовок (строка 0)
        int parsed = 0, skipped = 0;
        for (int row = 1; row < csvLines.Length; row++)
        {
            var line = csvLines[row];
            if (string.IsNullOrWhiteSpace(line)) { skipped++; continue; }

            var cols = SplitCsvLine(line);

            if (colIndex >= cols.Length)
            {
                SmartConLogger.Lookup($"    Строка {row}: colIndex={colIndex} >= cols.Length={cols.Length} — пропуск. Содержимое: '{line}'");
                skipped++;
                continue;
            }

            var cell = cols[colIndex].Trim().Trim('"');
            SmartConLogger.Lookup($"    Строка {row}: raw='{line}' | col[{colIndex}]='{cell}'");

            // Revit хранит значения в формате "50 mm", "50.0", etc.
            if (TryParseRevitValue(cell, out double value))
            {
                SmartConLogger.Lookup($"      → parsed={value:F3} мм");
                result.Add(value);
                parsed++;
            }
            else
            {
                SmartConLogger.Lookup($"      → НЕ УДАЛОСЬ распарсить '{cell}'");
                skipped++;
            }
        }

        var distinct = result.Distinct().OrderBy(v => v).ToList();
        SmartConLogger.Lookup($"    → Итого: распарсено={parsed}, пропущено={skipped}, уникальных={distinct.Count}");
        SmartConLogger.Lookup($"    → Уникальные значения (мм): [{string.Join(", ", distinct.Select(v => $"{v:F2}"))}]");
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
    private static string ResolveTableAlias(FamilyManager fm, string token)
    {
        if (token.StartsWith("\"") && token.EndsWith("\""))
            return token.Trim('"');
        foreach (FamilyParameter fp in fm.Parameters)
        {
            if (!string.Equals(fp.Definition?.Name, token, StringComparison.OrdinalIgnoreCase)) continue;
            var f = fp.Formula?.Trim();
            if (f is not null && f.StartsWith("\"") && f.EndsWith("\""))
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
    private static bool DependsOn(FamilyManager fm, string paramName, string target)
    {
        if (string.Equals(paramName, target, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (FamilyParameter fp in fm.Parameters)
        {
            if (!string.Equals(fp.Definition?.Name, paramName, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(fp.Formula)) return false;

            var vars = FormulaSolver.ExtractVariablesStatic(fp.Formula);
            if (vars.Count > 0)
                return vars.Any(v => string.Equals(v, target, StringComparison.OrdinalIgnoreCase));

            // ExtractVariablesStatic вернул [] — парсер не смог разобрать формулу
            // (имена переменных с пробелами, спецсимволы °, ³ и т.д.)
            // Fallback: проверяем содержит ли формула target как подстроку
            return fp.Formula.Contains(target, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }
}
