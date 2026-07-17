using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// v2.0.0: single-purpose service that allocates the canonical
/// <see cref="PrecomputedImportTriple"/> for a given
/// <see cref="displayName"/> without performing any file I/O.
/// </summary>
/// <remarks>
/// Splitting this off from <see cref="IFamilyImportService"/> keeps the
/// import pipeline (which writes files and updates the DB) separate from
/// the trivial DB lookup + path computation that every dialog row needs
/// to do up front. The precomputer is pure read-only — the
/// <see cref="PrecomputedImportTriple"/> it returns is later consumed by
/// the staging helpers and by <see cref="IFamilyImportService.ImportFileAsync"/>.
/// <para>
/// Callers that have to re-resolve the triple after the user renames a
/// row in the batch dialog (e.g.
/// <c>FamilyBatchImportViewModel.OnRowNameChanged</c>) re-enter through
/// this interface rather than re-deriving the values by hand, so the
/// derivation logic lives in exactly one place.
/// </para>
/// </remarks>
public interface IFamilyImportPrecomputer
{
    /// <summary>
    /// Build the canonical triple for the given display name.
    /// </summary>
    /// <param name="displayName">
    /// The name the user / Revit will see in the catalog. The implementation
    /// normalises this through <c>FamilyNameNormalizer</c> to find an
    /// existing item; if one is found its id + next version are reused,
    /// otherwise a fresh GUID and <c>"v1"</c> are allocated.
    /// </param>
    /// <param name="extension">
    /// The file extension including the leading dot (e.g. <c>".rfa"</c>
    /// for a loadable family, <c>".rvt"</c> for a system family project
    /// snapshot). <c>".rfa"</c> is the default for null/empty input.
    /// </param>
    /// <param name="forcedCatalogItemId">
    /// Issue #126: when the dedup service matched this row to an existing
    /// catalog item BY CONTENT HASH (possibly under a different name),
    /// the caller passes that item's id here so the triple targets the
    /// matched item (next version label + managed path under ITS folder)
    /// instead of resolving by name. When <c>null</c>, the legacy
    /// name-based resolution is used.
    /// </param>
    /// <param name="ct">Cancellation token forwarded to the DB lookup.</param>
    /// <returns>
    /// The triple, or <c>null</c> when the active database is missing /
    /// has no <c>GetDatabaseRoot()</c> — caller surfaces this as a user
    /// error rather than falling back to a temp folder (v2.0.0 has no temp
    /// folders, see ADR-035).
    /// </returns>
    Task<PrecomputedImportTriple?> BuildPrecomputedTripleAsync(
        string displayName,
        string extension,
        string? forcedCatalogItemId = null,
        CancellationToken ct = default);
}
