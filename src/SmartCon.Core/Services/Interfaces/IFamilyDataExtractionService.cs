using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public sealed record FamilyExtractionTypeResult(
    string TypeName,
    int SortOrder);

public sealed record FamilyExtractionValueResult(
    string ParameterName,
    AttributeScope? ParameterScope,
    string? StorageType,
    string? ValueText,
    string? ValueRaw,
    double? ValueNumber,
    string? UnitTypeId,
    AttributeValueStatus Status,
    string? Message);

/// <summary>
/// Extracted values of one type. For SYSTEM families the type identity
/// includes the family (Issue #191): two families in one category can
/// carry a same-named type («Стандарт» in both conduit families) — the
/// attribute pipeline must not collapse them.
/// </summary>
public sealed record FamilyExtractionTypeValues(
    string TypeName,
    int SortOrder,
    IReadOnlyList<FamilyExtractionValueResult> Values,
    string? FamilyName = null,
    string? FamilyKey = null);

public sealed record FamilyExtractionResult(
    bool Success,
    IReadOnlyList<FamilyExtractionTypeValues> Types,
    IReadOnlyList<FamilyExtractionValueResult>? UntypedValues,
    string? ErrorMessage,
    int RevitMajorVersion,
    IReadOnlyList<string>? SharedNestedFamilyNames = null)
{
    /// <summary>
    /// Non-null accessor for <see cref="SharedNestedFamilyNames"/>. The field
    /// is nullable to keep <c>new FamilyExtractionResult(...)</c> source-compatible
    /// for all call sites that pre-date the shared-nested fallback feature
    /// (ADR-034). Production call sites (RevitFamilyDataExtractionService and
    /// SmartCon.Revit system family paths) always populate it; legacy test
    /// fixtures and the SystemFamilyAttributeExtractionService may leave it
    /// null. Consumers should use this accessor to avoid repeating the
    /// null-coalesce at every read site.
    /// </summary>
    public IReadOnlyList<string> SharedNestedFamilyNamesSafe =>
        SharedNestedFamilyNames ?? Array.Empty<string>();
}

public interface IFamilyDataExtractionService
{
    /// <summary>
    /// Single entry point for all managed-storage import paths. Opens the
    /// .rfa via Revit API and reads the baked-in family types and parameters.
    /// Type Catalog types are expected to be already baked into the managed
    /// .rfa (ADR-033); this method does not simulate the catalog.
    ///
    /// Must be called on the Revit UI thread (I-01) — wraps <c>OpenDocumentFile</c>
    /// internally. Use <c>IFamilyManagerAwaitableEvent.RaiseAsync</c> at the
    /// call site to marshal onto the UI thread.
    /// </summary>
    /// <param name="managedRfaPath">Absolute path to the .rfa in managed storage.</param>
    /// <param name="expectedParameterNames">Parameter names to extract. Empty = all parameters.</param>
    /// <param name="ct">Cancellation token.</param>
    FamilyExtractionResult ExtractFromManagedFile(
        string managedRfaPath,
        IReadOnlyList<string> expectedParameterNames,
        CancellationToken ct = default);
}
