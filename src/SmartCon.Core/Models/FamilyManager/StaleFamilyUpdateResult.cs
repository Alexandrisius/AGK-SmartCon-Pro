namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// #222: outcome of a single-family stale update (<see cref="SmartCon.Core.Services.Interfaces.IStaleFamilyUpdater.UpdateFamilyAsync"/>).
/// Beyond the bare success flag it carries the per-type change report —
/// which catalog types actually changed values between the version that WAS
/// loaded (from the ES marker / stale snapshot) and the version updated TO,
/// split by the set of types currently loaded in the project. The report
/// keeps «успешно» from reading as «мой параметр обновился» when the changes
/// landed in types the project does not have loaded.
/// </summary>
/// <param name="Success">Whether the family is now at the catalog's current version.</param>
/// <param name="FamilyName">Resolved family name (for logs/progress), when known.</param>
/// <param name="ChangedLoadedTypeNames">Changed types that ARE loaded in the project.</param>
/// <param name="ChangedNotLoadedTypeNames">Changed types that are NOT loaded in the project.</param>
/// <param name="TypeDiffAvailable">False when the diff could not be computed honestly
/// (unknown from-version, missing extraction rows, optional dependency absent) —
/// the caller then shows the plain success message.</param>
/// <param name="ContentAlreadyCurrent">True when no reload happened because the embedded
/// content already matched the catalog target (pre-verify skip / failed-reload
/// arbitration) — «уже актуально», not «обновлено».</param>
public sealed record StaleFamilyUpdateResult(
    bool Success,
    string? FamilyName,
    IReadOnlyList<string> ChangedLoadedTypeNames,
    IReadOnlyList<string> ChangedNotLoadedTypeNames,
    bool TypeDiffAvailable,
    bool ContentAlreadyCurrent)
{
    /// <summary>Failed update (no marker written, the family stays stale).</summary>
    public static StaleFamilyUpdateResult Failure(string? familyName) =>
        new(false, familyName, [], [], false, false);

    /// <summary>Successful update without a per-type report (batch, system, orchestrated path).</summary>
    public static StaleFamilyUpdateResult SuccessWithoutReport(string? familyName, bool contentAlreadyCurrent) =>
        new(true, familyName, [], [], false, contentAlreadyCurrent);
}
