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
    /// The host must call <c>IStaleDetector.MarkUpdated([catalogItemId])</c>
    /// afterwards — the in-Revit state is the source of truth, the marker
    /// write is best-effort.
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
    /// <para>
///     Cancellation: if the caller cancels the <paramref name="ct"/>, the
///     loop stops at the next iteration boundary. The result will report
///     <c>SkippedCount &gt; 0</c> for the items that were not processed;
///     they remain in the snapshot and will be re-detected as stale on the
///     next Check.
/// </para>
///     <para>
///     Atomicity: a family is considered successful if Revit accepted the
///     load. The ES marker write is best-effort — if the marker write fails
///     after a successful load, the family is still reported as a success
///     (its in-Revit state has been updated, and re-running Update would
///     re-overwrite the parameters the user may have edited in the
///     meantime).
///     </para>
/// </summary>
    /// <param name="request">Batch parameters.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<StaleBatchUpdateResult> UpdateBatchAsync(
        StaleUpdateRequest request,
        IProgress<StaleBatchUpdateProgress>? progress = null,
        CancellationToken ct = default);
}
