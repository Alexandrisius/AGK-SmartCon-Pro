using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface ILoadableFamilyImportOrchestrator
{
    Task<LoadableFamilyImportResult> ImportAndPersistTypesAsync(
        IReadOnlyList<FamilyBatchImportItem> items,
        int targetRevitVersion,
        string? categoryId = null,
        CancellationToken ct = default);
}

public sealed record LoadableFamilyImportResult(
    bool Success,
    string? Message,
    int ImportedCount,
    int SkippedCount,
    IReadOnlyList<LoadableFamilyAttributeTask> AttributeTasks);

public sealed record LoadableFamilyAttributeTask(
    string CatalogItemId,
    string ManagedRfaPath,
    string? VersionId,
    string? FileId,
    bool HasTypeCatalog);
