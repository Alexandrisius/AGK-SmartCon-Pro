using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Logging;
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

    public IReadOnlyList<SizeOption> GetAvailableSizes(Document doc, ElementId elementId, int connectorIndex)
    {
        SmartConLogger.LookupSection("RevitDynamicSizeResolver.GetAvailableSizes");
        SmartConLogger.Lookup($"  elementId={elementId.Value}, connIdx={connectorIndex}");

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

        var sizes = TryGetLookupTableSizes(doc, elementId, connectorIndex);
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

    private List<SizeOption> TryGetLookupTableSizes(Document doc, ElementId elementId, int connectorIndex)
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
                return ExtractSizesFromFamily(familyDoc, element, connectorIndex);
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

    private List<SizeOption> ExtractSizesFromFamily(Document familyDoc, FamilyInstance instance, int connectorIndex)
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
                familyDoc, instanceTransform, targetOrigin);

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
            var rootRef = Core.Math.MiniFormulaSolver.SolveFor(formula, rootName, directRef);
            tableStoresDiameters = rootRef.HasValue && Math.Abs(rootRef.Value / refRadius - 2.0) < 0.1;
            SmartConLogger.Lookup($"  tableStoresDiameters={tableStoresDiameters} (SolveFor rootRef={rootRef?.ToString() ?? "null"})");
        }
        else
        {
            tableStoresDiameters = isDiameter;
            SmartConLogger.Lookup($"  tableStoresDiameters={tableStoresDiameters} (из FPA)");
        }

        var tableNames = fstm.GetAllSizeTableNames().ToList();
        var allValues = new SortedSet<double>();

        SmartConLogger.Lookup($"  Таблицы: [{string.Join(", ", tableNames)}]");

        foreach (var tableName in tableNames)
        {
            var colIndex = FindColumnIndex(fm, fstm, tableName, searchParamName);
            if (colIndex < 0)
            {
                SmartConLogger.Lookup($"  Таблица '{tableName}': colIndex не найден → пропуск");
                continue;
            }

            SmartConLogger.Lookup($"  Таблица '{tableName}': colIndex={colIndex}");

            var tempPath = Path.GetTempFileName();
            try
            {
                fstm.ExportSizeTable(tableName, tempPath);
                var lines = File.ReadAllLines(tempPath);
                var values = ExtractColumnValues(lines, colIndex, tableStoresDiameters);
                SmartConLogger.Lookup($"  Экспортировано {values.Count} значений из таблицы '{tableName}'");
                foreach (var v in values)
                    allValues.Add(v);
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

    private int FindColumnIndex(FamilyManager fm, FamilySizeTableManager fstm, string tableName, string searchParamName)
    {
        foreach (FamilyParameter fp in fm.Parameters)
        {
            if (string.IsNullOrEmpty(fp.Formula)) continue;

            var parsed = Core.Math.MiniFormulaSolver.ParseSizeLookup(fp.Formula);
            if (parsed is null) continue;

            var resolvedTableName = ResolveTableAlias(fm, parsed.Value.TableName);
            if (!string.Equals(resolvedTableName, tableName, StringComparison.OrdinalIgnoreCase))
                continue;

            var queryParams = parsed.Value.QueryParameters;
            for (int i = 0; i < queryParams.Count; i++)
            {
                if (string.Equals(queryParams[i], searchParamName, StringComparison.OrdinalIgnoreCase)
                    || DependsOn(fm, queryParams[i], searchParamName))
                {
                    return i + 1;
                }
            }
        }
        return -1;
    }

    private List<double> ExtractColumnValues(string[] csvLines, int colIndex, bool tableStoresDiameters)
    {
        var result = new List<double>();
        if (csvLines.Length == 0) return result;

        for (int row = 1; row < csvLines.Length; row++)
        {
            var line = csvLines[row];
            if (string.IsNullOrWhiteSpace(line)) continue;

            var cols = line.Split(',');
            if (colIndex >= cols.Length) continue;

            var cell = cols[colIndex].Trim().Trim('"');
            if (TryParseRevitValue(cell, out double value))
            {
                double radius = tableStoresDiameters ? value / 2.0 : value;
                double radiusInFeet = radius / 304.8;
                result.Add(radiusInFeet);
            }
        }

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

    private static bool DependsOn(FamilyManager fm, string paramName, string target)
    {
        if (string.Equals(paramName, target, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (FamilyParameter fp in fm.Parameters)
        {
            if (!string.Equals(fp.Definition?.Name, paramName, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(fp.Formula)) return false;
            var vars = Core.Math.MiniFormulaSolver.ExtractVariables(fp.Formula);
            return vars.Any(v => string.Equals(v, target, StringComparison.OrdinalIgnoreCase));
        }
        return false;
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

        foreach (var symbolId in symbolIds)
        {
            using var st = new SubTransaction(doc);
            try
            {
                st.Start();

                var inst = doc.GetElement(instance.Id) as FamilyInstance;
                if (inst is null) { st.RollBack(); continue; }

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

                st.RollBack();
            }
            catch (Exception ex)
            {
                SmartConLogger.Lookup($"  ИСКЛЮЧЕНИЕ symbolId={symbolId.Value}: {ex.Message}");
                try { st.RollBack(); } catch { }
            }
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
}
