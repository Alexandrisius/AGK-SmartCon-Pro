namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Progress payload of a stale CHECK run (manual «Проверить» via
/// <c>IStaleDetector.CheckCategoryAsync</c> and the post-import check loop):
/// one report per family verified against the catalog. Mirrors
/// <see cref="StaleBatchUpdateProgress"/> (batch update) for the check flows;
/// the dockable pane renders it as the thin bottom progress bar plus the
/// «Проверка X из Y — имя» status text, so background ExternalEvent work is
/// visible instead of looking like a phantom Revit freeze.
/// </summary>
/// <param name="Completed">Number of items verified so far.</param>
/// <param name="Total">Total number of items to verify. Refined as matching
/// progresses (only families actually loaded in the project are verified).</param>
/// <param name="CurrentFamilyName">Display name of the family just verified.</param>
public sealed record StaleCheckProgress(
    int Completed,
    int Total,
    string CurrentFamilyName);
