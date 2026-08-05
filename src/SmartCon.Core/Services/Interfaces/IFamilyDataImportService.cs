using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public sealed record FamilyDataImportResult(
    bool Success,
    string? RunId,
    int TypesCount,
    int AttributesFoundCount,
    int AttributesMissingCount,
    string? ErrorMessage);

public sealed record FamilyExtractionPrepareResult(
    bool Success,
    FamilyCatalogItem? Item,
    string? ResolvedFilePath,
    IReadOnlyList<string> ParameterNames,
    string? ErrorMessage);

public interface IFamilyDataImportService
{
    Task<FamilyDataImportResult> ImportDataAsync(string catalogItemId, CancellationToken ct = default);
    Task<FamilyExtractionPrepareResult> PrepareExtractionAsync(string catalogItemId, int targetRevitVersion, CancellationToken ct = default);
    Task<FamilyDataImportResult> SaveExtractionResultAsync(string catalogItemId, FamilyExtractionResult extractionResult, string? versionId, string? fileId, CancellationToken ct = default);
}
