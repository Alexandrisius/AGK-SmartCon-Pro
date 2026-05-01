namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Progress report for batch import operation.
/// </summary>
/// <param name="CurrentFileIndex">Zero-based index of the current file.</param>
/// <param name="TotalFiles">Total number of files to import.</param>
/// <param name="CurrentFileName">Name of the file being processed.</param>
/// <param name="SuccessCount">Files imported so far.</param>
/// <param name="SkippedCount">Files skipped so far.</param>
/// <param name="ErrorCount">Errors so far.</param>
public sealed record FamilyImportProgress(
    int CurrentFileIndex,
    int TotalFiles,
    string CurrentFileName,
    int SuccessCount,
    int SkippedCount,
    int ErrorCount);
