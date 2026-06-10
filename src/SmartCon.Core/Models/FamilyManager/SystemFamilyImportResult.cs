namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Result of the final system-family import pipeline: takes prepared .rvt files,
/// imports them into managed storage, and returns extraction tasks for the
/// background <see cref="ISystemFamilyAttributeExtractionService"/>.
/// </summary>
public sealed record SystemFamilyImportResult(
    bool Success,
    string? Message,
    IReadOnlyList<SystemFamilyExtractionTask> ExtractionTasks,
    int TypesCount);
