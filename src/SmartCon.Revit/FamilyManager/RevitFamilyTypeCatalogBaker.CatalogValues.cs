using System.IO;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Compatibility;
using SmartCon.Revit.Util;

namespace SmartCon.Revit.FamilyManager;

public sealed partial class RevitFamilyTypeCatalogBaker
{
    private BakeStats CreateCatalogTypes(
        Autodesk.Revit.DB.FamilyManager fm,
        TypeCatalogParseResult catalog,
        Dictionary<string, FamilyParameter> paramMap,
        Dictionary<string, TypeCatalogColumn> catalogColumnsByName,
        CancellationToken ct)
    {
        var createdCount = 0;
        var unitConverted = 0;
        var unitFailed = 0;

        foreach (var entry in catalog.Entries)
        {
            ct.ThrowIfCancellationRequested();

            var newType = fm.NewType(entry.TypeName);
            if (newType is null)
            {
                SmartConLogger.Warn($"NewType returned null for '{entry.TypeName}' [Action: skipping type]");
                continue;
            }

            fm.CurrentType = newType;
            var entryStats = ApplyCatalogValues(fm, entry, paramMap, catalogColumnsByName);
            unitConverted += entryStats.Converted;
            unitFailed += entryStats.Failed;
            createdCount++;
        }

        return new BakeStats(createdCount, unitConverted, unitFailed);
    }

    private BakeStats ApplyCatalogValues(
        Autodesk.Revit.DB.FamilyManager fm,
        TypeCatalogEntry entry,
        Dictionary<string, FamilyParameter> paramMap,
        Dictionary<string, TypeCatalogColumn> catalogColumnsByName)
    {
        var unitConverted = 0;
        var unitFailed = 0;

        foreach (var kvp in entry.ParameterValues)
        {
            var columnName = kvp.Key;
            var rawValue = kvp.Value;

            if (!paramMap.TryGetValue(columnName, out var param) || param is null)
            {
                // Skip-debug: срабатывает только когда column .txt не имеет соответствующего
                // параметра в .rfa (не hot path — обычно 0 случаев на корректный каталог).
                SmartConLogger.Debug($"Skip(no-param): type='{entry.TypeName}' param='{columnName}'");
                continue;
            }

            if (param.IsReadOnly)
            {
                SmartConLogger.Debug($"Skip(readonly): type='{entry.TypeName}' param='{columnName}'");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(param.Formula))
            {
                SmartConLogger.Debug(
                    $"Skip(formula-driven): type='{entry.TypeName}' param='{columnName}'");
                continue;
            }

            var storageTypeCode = MapRevitStorageType(param.StorageType);
            var applyResult = _valueApplier.Apply(rawValue, storageTypeCode);
            if (applyResult.Status != TypeCatalogValueApplyStatus.Success)
            {
                SmartConLogger.Warn(
                    $"Value apply failed: type='{entry.TypeName}' param='{columnName}' " +
                    $"value='{rawValue}' status={applyResult.Status} err='{applyResult.Error}' " +
                    "[Action: skipping parameter, continuing with type]");
                continue;
            }

            try
            {
                var conversionResult = TryConvertUnit(
                    param, applyResult.Value, columnName, entry.TypeName, catalogColumnsByName);

                switch (conversionResult.Outcome)
                {
                    case UnitConversionOutcome.Skipped:
                        ApplyTypedValue(fm, param, conversionResult.FinalValue);
                        break;
                    case UnitConversionOutcome.Converted:
                        ApplyTypedValue(fm, param, conversionResult.FinalValue);
                        unitConverted++;
                        break;
                    case UnitConversionOutcome.Failed:
                        // Warn уже залогирован в TryConvertUnit.
                        unitFailed++;
                        break;
                }
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"Set failed: type='{entry.TypeName}' param='{columnName}' " +
                    $"ex={ex.GetType().Name}: {ex.Message} [Action: skipping parameter, continuing with type]");
            }
        }

        return new BakeStats(CreatedCount: 0, Converted: unitConverted, Failed: unitFailed);
    }

    /// <summary>
    /// Конвертирует Double значение в Revit internal units, если column header
    /// содержит <c>##UNIT##</c> annotation. Для Integer/String/ElementId — без изменений.
    /// Возвращает <see cref="UnitConversionResult"/> с одним из трёх исходов:
    /// <list type="bullet">
    ///   <item><see cref="UnitConversionOutcome.Skipped"/> — не Double или нет annotation; вернуть значение как есть.</item>
    ///   <item><see cref="UnitConversionOutcome.Converted"/> — успешная конверсия; вернуть конвертированное.</item>
    ///   <item><see cref="UnitConversionOutcome.Failed"/> — unit annotation не распознана; caller skip parameter.</item>
    /// </list>
    /// </summary>
    private static UnitConversionResult TryConvertUnit(
        FamilyParameter param,
        object? applyResultValue,
        string columnName,
        string entryTypeName,
        Dictionary<string, TypeCatalogColumn> catalogColumnsByName)
    {
        if (applyResultValue is null)
            return UnitConversionResult.Failed();

        // Unit conversion только для Double storage type с явной ##UNIT## annotation.
        if (param.StorageType != StorageType.Double)
            return UnitConversionResult.Skipped(applyResultValue);

        if (!catalogColumnsByName.TryGetValue(columnName, out var column) || column is null)
            return UnitConversionResult.Skipped(applyResultValue);

        if (!column.HasUnitAnnotation)
            return UnitConversionResult.Skipped(applyResultValue);

        var rawDouble = (double)applyResultValue;
        var internalValue = RevitUnitsCompat.CatalogCellToInternalUnits(
            rawDouble, column.UnitAnnotation, param);

        if (internalValue is null)
        {
            SmartConLogger.Warn(
                $"Unit annotation '{column.UnitAnnotation}' (##TYPE##={column.TypeAnnotation ?? "<none>"}) " +
                $"not recognized for type='{entryTypeName}' param='{columnName}' " +
                $"[Action: verify the .txt header matches Revit Type Catalog spec, " +
                $"or remove the ##TYPE##UNITS annotation to use project display units]");
            return UnitConversionResult.Failed();
        }

        return UnitConversionResult.Converted(internalValue.Value);
    }

    private static object? ReadParameterValue(FamilyType familyType, FamilyParameter param)
    {
        if (!familyType.HasValue(param))
        {
            return null;
        }

        return param.StorageType switch
        {
            StorageType.String => familyType.AsString(param),
            StorageType.Double => familyType.AsDouble(param),
            StorageType.Integer => familyType.AsInteger(param),
            StorageType.ElementId => familyType.AsElementId(param),
            _ => null
        };
    }

    private static void ApplyTypedValue(Autodesk.Revit.DB.FamilyManager fm, FamilyParameter param, object? value)
    {
        switch (param.StorageType)
        {
            case StorageType.String:
                fm.Set(param, (string)value!);
                break;
            case StorageType.Double:
                fm.Set(param, (double)value!);
                break;
            case StorageType.Integer:
                fm.Set(param, (int)value!);
                break;
            case StorageType.ElementId:
#if REVIT2024_OR_GREATER
                fm.Set(param, new ElementId((long)value!));
#elif REVIT2022_OR_GREATER
                fm.Set(param, new ElementId((int)value!));
#else
                fm.Set(param, new ElementId((BuiltInParameter)(long)value!));
#endif
                break;
            default:
                throw new NotSupportedException($"StorageType '{param.StorageType}' is not supported");
        }
    }

    private static StorageTypeCode MapRevitStorageType(StorageType rt)
    {
        return (int)rt switch
        {
            0 => StorageTypeCode.StgNone,
            1 => StorageTypeCode.StgInt,
            2 => StorageTypeCode.StgNumber,
            3 => StorageTypeCode.StgText,
            4 => StorageTypeCode.StgElementId,
            _ => StorageTypeCode.StgNone,
        };
    }
}
