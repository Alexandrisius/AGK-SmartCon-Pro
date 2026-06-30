using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface IFamilyDataImportRunRepository
{
    Task<FamilyDataImportRun?> GetLatestRunAsync(string catalogItemId, CancellationToken ct = default);

    /// <summary>
    /// Returns the most recent successful import run for the
    /// <b>active version</b> of the given catalog item. The active version
    /// is resolved via <c>catalog_items.current_version_label</c> →
    /// <c>catalog_versions.id</c> (the same lookup as
    /// <see cref="IFamilyCatalogProvider.GetVersionByLabelAsync"/>).
    ///
    /// Use this instead of <see cref="GetLatestRunAsync"/> when the UI
    /// needs to reflect the active version's import run (date, Revit
    /// version, types count) — for example after
    /// <c>SetActiveVersionAsync</c> rolled back to an older version,
    /// <see cref="GetLatestRunAsync"/> would still return the latest
    /// import run by date, which may belong to a now-inactive version.
    /// </summary>
    Task<FamilyDataImportRun?> GetLatestRunForActiveVersionAsync(string catalogItemId, CancellationToken ct = default);

    Task<IReadOnlyList<FamilyDataImportRun>> GetRunsForItemAsync(string catalogItemId, CancellationToken ct = default);
    Task<FamilyDataImportRun> CreateRunAsync(FamilyDataImportRun run, CancellationToken ct = default);
    Task<FamilyDataImportRun> UpdateRunAsync(string runId, FamilyDataImportStatus status, int typesCount, DateTimeOffset completedAtUtc, string? errorMessage, CancellationToken ct = default);
}
