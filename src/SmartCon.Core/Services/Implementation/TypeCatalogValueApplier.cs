using System.Globalization;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Core.Services.Implementation;

/// <summary>
/// Pure C# implementation of <see cref="ITypeCatalogValueApplier"/>. Parses
/// raw .txt values into typed values matching RevitAPI's <c>StorageType</c>
/// (via <see cref="StorageTypeCode"/>).
///
/// Strategy:
/// <list type="bullet">
///   <item><b>String</b> — value returned as-is.</item>
///   <item><b>Integer</b> — <c>int.Parse</c> with <see cref="CultureInfo.InvariantCulture"/>.</item>
///   <item><b>Double</b> — try <see cref="CultureInfo.InvariantCulture"/> first, fall back to
///     <see cref="CultureInfo.CurrentCulture"/> (some .txt files use comma decimals in RU locale).</item>
///   <item><b>ElementId</b> — <c>long.Parse</c> with InvariantCulture (the raw id).</item>
///   <item>Anything else — <see cref="TypeCatalogValueApplyStatus.UnsupportedStorageType"/>.</item>
/// </list>
/// </summary>
public sealed class TypeCatalogValueApplier : ITypeCatalogValueApplier
{
    public TypeCatalogValueApplyResult Apply(string? rawValue, StorageTypeCode storageType)
    {
        if (rawValue is null)
        {
            return new TypeCatalogValueApplyResult(
                TypeCatalogValueApplyStatus.InvalidFormat,
                null,
                "Value is null");
        }

        switch (storageType)
        {
            case StorageTypeCode.StgText:
                return new TypeCatalogValueApplyResult(
                    TypeCatalogValueApplyStatus.Success,
                    rawValue,
                    null);

            case StorageTypeCode.StgInt:
                if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iVal))
                {
                    return new TypeCatalogValueApplyResult(
                        TypeCatalogValueApplyStatus.Success,
                        iVal,
                        null);
                }
                return new TypeCatalogValueApplyResult(
                    TypeCatalogValueApplyStatus.InvalidFormat,
                    null,
                    $"Cannot parse '{rawValue}' as whole number");

            case StorageTypeCode.StgNumber:
                if (double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var dVal))
                {
                    return new TypeCatalogValueApplyResult(
                        TypeCatalogValueApplyStatus.Success,
                        dVal,
                        null);
                }
                if (double.TryParse(rawValue, NumberStyles.Float, CultureInfo.CurrentCulture, out dVal))
                {
                    return new TypeCatalogValueApplyResult(
                        TypeCatalogValueApplyStatus.Success,
                        dVal,
                        null);
                }
                return new TypeCatalogValueApplyResult(
                    TypeCatalogValueApplyStatus.InvalidFormat,
                    null,
                    $"Cannot parse '{rawValue}' as real number");

            case StorageTypeCode.StgElementId:
                if (long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idVal))
                {
                    return new TypeCatalogValueApplyResult(
                        TypeCatalogValueApplyStatus.Success,
                        idVal,
                        null);
                }
                return new TypeCatalogValueApplyResult(
                    TypeCatalogValueApplyStatus.InvalidFormat,
                    null,
                    $"Cannot parse '{rawValue}' as ElementId");

            default:
                return new TypeCatalogValueApplyResult(
                    TypeCatalogValueApplyStatus.UnsupportedStorageType,
                    null,
                    $"StorageType code '{(int)storageType}' is not supported");
        }
    }
}
