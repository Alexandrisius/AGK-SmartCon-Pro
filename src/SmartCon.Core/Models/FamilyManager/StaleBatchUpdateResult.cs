namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Aggregate result of a batch update.
/// </summary>
/// <param name="TotalRequested">
/// Total number of catalog items requested.
/// <para>
/// NOTE: this is the size of the request at submission time, not the number of
/// items actually processed. If the caller cancelled the operation,
/// <see cref="SkippedCount"/> will be greater than zero and the invariant
/// <c>TotalRequested == SuccessCount + FailedCount + SkippedCount</c> holds.
/// </para>
/// </param>
/// <param name="SuccessCount">Number of items successfully updated.</param>
/// <param name="FailedCount">Number of items that failed.</param>
/// <param name="SkippedCount">
/// Number of items not processed because the cancellation token was tripped
/// before the loop reached them. Zero when the batch completed normally.
/// </param>
/// <param name="SuccessCatalogItemIds">
/// Catalog item IDs that were successfully updated. Used by the caller to
/// remove the corresponding entries from the stale snapshot (so the next
/// <c>Check</c> re-evaluates them from scratch).
/// </param>
/// <param name="FailedCatalogItemIds">Catalog item IDs that failed (for retry).</param>
public sealed record StaleBatchUpdateResult(
    int TotalRequested,
    int SuccessCount,
    int FailedCount,
    int SkippedCount,
    IReadOnlyList<string> SuccessCatalogItemIds,
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
