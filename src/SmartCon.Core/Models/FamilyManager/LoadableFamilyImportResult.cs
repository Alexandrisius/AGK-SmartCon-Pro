namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Result of <see cref="ILoadableFamilyImportOrchestrator.ImportAndPersistTypesAsync"/>.
/// </summary>
public sealed record LoadableFamilyImportResult(
    bool Success,
    string? Message,
    int ImportedCount,
    int SkippedCount,
    IReadOnlyList<LoadableFamilyAttributeTask> AttributeTasks);
