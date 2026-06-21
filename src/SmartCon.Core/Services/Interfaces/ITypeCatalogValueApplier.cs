namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Result of applying a string value from a Type Catalog (.txt) to a Revit
/// <c>StorageType</c> target. The <see cref="StorageTypeCode"/> uses the
/// same integer values as RevitAPI's <c>StorageType</c> enum, so callers
/// can convert with a simple cast: <c>(StorageTypeCode)(int)param.StorageType</c>.
/// </summary>
/// <param name="Status">Outcome of the apply attempt.</param>
/// <param name="Value">
/// On <see cref="TypeCatalogValueApplyStatus.Success"/>: boxed typed value
/// (<c>string</c> for Text, <c>double</c> for Number, <c>int</c> for Int,
/// <c>long</c> for ElementId raw id). <c>null</c> for non-success.
/// </param>
/// <param name="Error">Human-readable error description on failure; <c>null</c> on success.</param>
public sealed record TypeCatalogValueApplyResult(
    TypeCatalogValueApplyStatus Status,
    object? Value,
    string? Error);

/// <summary>Outcome of <see cref="ITypeCatalogValueApplier.Apply"/>.</summary>
public enum TypeCatalogValueApplyStatus
{
    /// <summary>Value parsed successfully.</summary>
    Success,

    /// <summary>Value could not be parsed into the expected CLR type.</summary>
    InvalidFormat,

    /// <summary>StorageType code is not supported (e.g. None, or unknown).</summary>
    UnsupportedStorageType,
}

/// <summary>
/// Integer codes matching Revit API <c>StorageType</c> enum exactly. The underlying
/// integer values are STABLE across all Revit versions (2019 through 2026+):
/// <c>None=0, Integer=1, Double=2, String=3, ElementId=4</c>. Confirmed via
/// <see href="https://www.revitapidocs.com/2025/3dbebcb8-792b-a3dd-fe63-faaa05704f3c.htm"/>.
/// Direct cast at the call site: <c>(StorageTypeCode)(int)param.StorageType</c>.
/// </summary>
public enum StorageTypeCode
{
    /// <summary>No storage type (RevitAPI <c>StorageType.None</c> = 0).</summary>
    StgNone = 0,
    /// <summary>Whole-number value (RevitAPI <c>StorageType.Integer</c> = 1).</summary>
    StgInt = 1,
    /// <summary>Real-number value (RevitAPI <c>StorageType.Double</c> = 2).</summary>
    StgNumber = 2,
    /// <summary>Text value (RevitAPI <c>StorageType.String</c> = 3).</summary>
    StgText = 3,
    /// <summary>ElementId reference (RevitAPI <c>StorageType.ElementId</c> = 4).</summary>
    StgElementId = 4,
}

/// <summary>
/// Converts a raw string value from a Type Catalog (.txt) row into a typed
/// value suitable for <c>FamilyManager.Set</c>. Pure C# — no Revit API calls,
/// no RevitAPI dependency, allowing unit tests without the Revit host
/// (I-09). Uses <see cref="StorageTypeCode"/> (int-backed) instead of
/// RevitAPI's <c>StorageType</c> enum for the same reason.
/// </summary>
public interface ITypeCatalogValueApplier
{
    /// <summary>
    /// Parses <paramref name="rawValue"/> according to <paramref name="storageType"/>.
    /// </summary>
    /// <param name="rawValue">String value from the .txt file.</param>
    /// <param name="storageType">
    /// Target parameter's Revit StorageType, passed as a
    /// <see cref="StorageTypeCode"/> (use <c>(StorageTypeCode)(int)param.StorageType</c>).
    /// </param>
    /// <returns>
    /// <see cref="TypeCatalogValueApplyResult"/> with Status and (on Success) Value.
    /// Never throws — invalid input is reported as <see cref="TypeCatalogValueApplyStatus.InvalidFormat"/>.
    /// </returns>
    TypeCatalogValueApplyResult Apply(string? rawValue, StorageTypeCode storageType);
}
