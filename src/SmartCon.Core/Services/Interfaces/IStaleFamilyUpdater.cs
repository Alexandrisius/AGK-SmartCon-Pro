using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Updates families in the active project by reloading the catalog's current version
/// (ADR-030, Issue #69 AC). After a successful update, writes a new
/// <see cref="FamilyVersion"/> marker to the loaded family via
/// <see cref="IFamilyVersionStore"/>.
/// </summary>
public interface IStaleFamilyUpdater
{
    /// <summary>
    /// Update a single family. Returns <c>true</c> on success, <c>false</c> otherwise.
    /// The host must call <c>IStaleDetector.InvalidateCache()</c> afterwards.
    /// </summary>
    /// <param name="catalogItemId">Catalog item to update.</param>
    /// <param name="overwriteParameterValues">
    /// Passed to <c>FamilyLoadOptions.OverwriteParameterValues</c>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> UpdateFamilyAsync(
        string catalogItemId,
        bool overwriteParameterValues,
        CancellationToken ct);

    /// <summary>
    /// Update a batch of families sequentially. Reports progress through <paramref name="progress"/>
    /// on every family. Returns aggregate result with failed IDs for retry.
    /// </summary>
    Task<StaleBatchUpdateResult> UpdateBatchAsync(
        StaleUpdateRequest request,
        IProgress<StaleBatchUpdateProgress>? progress = null,
        CancellationToken ct = default);
}
