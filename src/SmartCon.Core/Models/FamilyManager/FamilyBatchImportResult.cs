namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Result of importing multiple family files.
/// </summary>
/// <param name="Results">Per-file results.</param>
/// <param name="TotalFiles">Total files processed.</param>
/// <param name="SuccessCount">Successfully imported.</param>
/// <param name="SkippedCount">Skipped as duplicates.</param>
/// <param name="ErrorCount">Failed with errors.</param>
public sealed record FamilyBatchImportResult(
    IReadOnlyList<FamilyImportResult> Results,
    int TotalFiles,
    int SuccessCount,
    int SkippedCount,
    int ErrorCount);
