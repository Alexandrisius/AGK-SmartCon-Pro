namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Aggregate result of a batch update.
/// </summary>
/// <param name="TotalRequested">Total number of catalog items requested.</param>
/// <param name="SuccessCount">Number of items successfully updated.</param>
/// <param name="FailedCount">Number of items that failed.</param>
/// <param name="FailedCatalogItemIds">Catalog item IDs that failed (for retry).</param>
public sealed record StaleBatchUpdateResult(
    int TotalRequested,
    int SuccessCount,
    int FailedCount,
    IReadOnlyList<string> FailedCatalogItemIds);

/// <summary>
/// Progress event payload for a batch update.
/// </summary>
/// <param name="Completed">Number of items processed so far.</param>
/// <param name="Total">Total number of items to process.</param>
/// <param name="CurrentFamilyName">Display name of the family currently being processed.</param>
public sealed record StaleBatchUpdateProgress(
    int Completed,
    int Total,
    string CurrentFamilyName);
