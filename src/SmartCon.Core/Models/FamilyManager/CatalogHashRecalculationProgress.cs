namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Progress of the catalog hash-recalculation migration (Issue #126).
/// Reported once per processed file.
/// </summary>
/// <param name="Current">1-based index of the file being processed.</param>
/// <param name="Total">Total number of files to process.</param>
/// <param name="CurrentFileName">File name of the version being
/// recalculated (basename only, for display).</param>
public sealed record CatalogHashRecalculationProgress(
    int Current,
    int Total,
    string CurrentFileName);
