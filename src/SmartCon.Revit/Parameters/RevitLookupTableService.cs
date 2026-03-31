using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Math;
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
        var ctx = BuildLookupContext(doc, elementId, connectorIndex);
        if (ctx is null) return false;

        var values = ExtractColumnValues(ctx.CsvLines, ctx.ColIndex);
        double targetMm = ToMillimeters(radiusInternalUnits, ctx.IsRadius);

        return values.Any(v => System.Math.Abs(v - targetMm) < 0.02); // 0.02 мм допуск
    }

    public double GetNearestAvailableRadius(Document doc, ElementId elementId,
        int connectorIndex, double targetRadiusInternalUnits)
    {
        var ctx = BuildLookupContext(doc, elementId, connectorIndex);
        if (ctx is null) return targetRadiusInternalUnits;

        var values = ExtractColumnValues(ctx.CsvLines, ctx.ColIndex);
        if (values.Count == 0) return targetRadiusInternalUnits;

        double targetMm = ToMillimeters(targetRadiusInternalUnits, ctx.IsRadius);

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

        // Конвертировать обратно в internal units
        return FromMillimeters(nearestMm, ctx.IsRadius);
    }

    public bool HasLookupTable(Document doc, ElementId elementId, int connectorIndex)
        => BuildLookupContext(doc, elementId, connectorIndex) is not null;

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
        var element = doc.GetElement(elementId);
        if (element is null) return null;

        // MEP Curve не имеет LookupTable
        if (element is MEPCurve or FlexPipe) return null;

        if (element is not FamilyInstance instance) return null;
        RevitFamily? family = instance.Symbol?.Family;
        if (family is null) return null;

        if (doc.IsModifiable)
        {
            Debug.WriteLine("[SmartCon][LookupSvc] BuildLookupContext вызван внутри транзакции — запрещено");
            return null;
        }

        // Получить origin коннектора для FamilyParameterAnalyzer
        var cm        = instance.MEPModel?.ConnectorManager;
        var connector = cm?.FindByIndex(connectorIndex);
        if (connector is null) return null;

        var targetOriginGlobal = connector.CoordinateSystem.Origin;
        var instanceTransform  = instance.GetTransform();

        Document? familyDoc = null;
        try
        {
            familyDoc = doc.EditFamily(family);
            return BuildLookupContextFromFamily(familyDoc, instanceTransform, targetOriginGlobal);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SmartCon][LookupSvc] BuildLookupContext failed: {ex.Message}");
            return null;
        }
        finally
        {
            familyDoc?.Close(false);
        }
    }

    private static LookupContext? BuildLookupContextFromFamily(
        Document familyDoc,
        RevitTransform instanceTransform,
        XYZ targetOriginGlobal)
    {
        // Получить FamilySizeTableManager
        var fstm = FamilySizeTableManager.GetFamilySizeTableManager(
            familyDoc, familyDoc.OwnerFamily.Id);

        if (fstm is null || fstm.NumberOfSizeTables == 0)
        {
            Debug.WriteLine("[SmartCon][LookupSvc] No size tables in family");
            return null;
        }

        // Анализ параметра коннектора
        var (directName, rootName, formula, _) =
            FamilyParameterAnalyzer.AnalyzeConnectorRadiusParam(
                familyDoc, instanceTransform, targetOriginGlobal);

        // Параметр поиска — корневой (если есть формула) или прямой
        var searchParamName = rootName ?? directName;
        if (searchParamName is null)
        {
            Debug.WriteLine("[SmartCon][LookupSvc] searchParamName is null");
            return null;
        }

        // Определяем: значения в таблице — радиусы или диаметры?
        // Если directParam имеет формулу "x / 2", значит x = diameter → таблица хранит диаметры.
        bool tableStoresDiameters = formula is not null &&
            formula.Contains("/") &&
            formula.Contains("2");

        // Перебрать все таблицы и найти ту, где searchParamName участвует как queryParam
        foreach (var tableName in fstm.GetAllSizeTableNames())
        {
            var ctx = TryGetContextForTable(familyDoc, fstm, tableName, searchParamName, tableStoresDiameters);
            if (ctx is not null) return ctx;
        }

        Debug.WriteLine($"[SmartCon][LookupSvc] No size table found for param '{searchParamName}'");
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

        foreach (FamilyParameter fp in fm.Parameters)
        {
            if (string.IsNullOrEmpty(fp.Formula)) continue;

            var parsed = MiniFormulaSolver.ParseSizeLookup(fp.Formula);
            if (parsed is null) continue;

            if (!string.Equals(parsed.Value.TableName, tableName, StringComparison.OrdinalIgnoreCase))
                continue;

            // Найти позицию searchParamName среди QueryParameters
            var queryParams = parsed.Value.QueryParameters;
            for (int i = 0; i < queryParams.Count; i++)
            {
                if (string.Equals(queryParams[i], searchParamName, StringComparison.OrdinalIgnoreCase))
                {
                    // colIndex в CSV: col[0] = комментарии, col[queryParamIndex+1] = значения param
                    colIndex = i + 1;
                    break;
                }
            }

            if (colIndex >= 0)
            {
                Debug.WriteLine($"[SmartCon][LookupSvc] Found table='{tableName}', param='{searchParamName}', colIndex={colIndex}");
                break;
            }
        }

        if (colIndex < 0) return null;

        // Экспортировать таблицу в temp CSV и прочитать
        var tempPath = Path.GetTempFileName();
        try
        {
            fstm.ExportSizeTable(tableName, tempPath);
            var lines = File.ReadAllLines(tempPath);
            Debug.WriteLine($"[SmartCon][LookupSvc] Exported table '{tableName}': {lines.Length} lines");
            return new LookupContext(lines, colIndex, !tableStoresDiameters);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SmartCon][LookupSvc] ExportSizeTable failed: {ex.Message}");
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

        // Пропускаем заголовок (строка 0)
        for (int row = 1; row < csvLines.Length; row++)
        {
            var line = csvLines[row];
            if (string.IsNullOrWhiteSpace(line)) continue;

            var cols = SplitCsvLine(line);
            if (colIndex >= cols.Length) continue;

            var cell = cols[colIndex].Trim().Trim('"');
            // Revit хранит значения в формате "50 mm", "50.0", etc.
            // Парсим числовую часть
            if (TryParseRevitValue(cell, out double value))
                result.Add(value);
        }

        return result.Distinct().OrderBy(v => v).ToList();
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
}
