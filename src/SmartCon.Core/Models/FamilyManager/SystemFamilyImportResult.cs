namespace SmartCon.Core.Models.FamilyManager;

public sealed record SystemFamilyImportResult(
    bool Success,
    string? Message,
    IReadOnlyList<SystemFamilyExtractionTask> ExtractionTasks,
    int TypesCount);

public sealed record SystemFamilyExtractionTask(
    string CatalogItemId,
    string TempRvtPath,
    IReadOnlyList<string> TypeNames,
    string? VersionId,
    string? FileId);
