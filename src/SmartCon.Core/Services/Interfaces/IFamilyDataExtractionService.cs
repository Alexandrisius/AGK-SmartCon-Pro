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

public sealed record FamilyExtractionTypeValues(
    string TypeName,
    int SortOrder,
    IReadOnlyList<FamilyExtractionValueResult> Values);

public sealed record FamilyExtractionResult(
    bool Success,
    IReadOnlyList<FamilyExtractionTypeValues> Types,
    IReadOnlyList<FamilyExtractionValueResult>? UntypedValues,
    string? ErrorMessage,
    int RevitMajorVersion);

public interface IFamilyDataExtractionService
{
    FamilyExtractionResult Extract(string rfaFilePath, IReadOnlyList<string> expectedParameterNames);

    /// <summary>
    /// Extracts types and parameters from an already-open family document.
    /// The caller is responsible for closing the document.
    /// </summary>
    FamilyExtractionResult Extract(Autodesk.Revit.DB.Document familyDocument, IReadOnlyList<string> expectedParameterNames);

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
