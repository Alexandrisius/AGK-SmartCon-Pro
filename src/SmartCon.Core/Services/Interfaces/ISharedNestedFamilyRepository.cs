namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Persists the names of shared nested families declared by a loadable family
/// in the local catalog. Populated at import time by
/// <see cref="IFamilyDataExtractionService.ExtractFromManagedFile"/> (the
/// extraction runs inside the same OpenDocumentFile+Close cycle as the
/// type/parameter scan, see ADR-034 §2) and consumed at load time by
/// <c>IFamilyLoadService</c> to provide a fallback name when the Revit API
/// returns <c>null</c> for the shared nested family reference (REVIT-198137,
/// affects Revit 2023 and 2024 prior to 24.3.0.13).
/// </summary>
public interface ISharedNestedFamilyRepository
{
    /// <summary>
    /// Replaces the entire list of shared nested family names for the given
    /// (catalog item, version) pair. Any previously stored names for that
    /// version are removed. Names that differ only in case are deduplicated
    /// (case-insensitive comparison, mirroring Revit's own behaviour).
    /// </summary>
    /// <param name="catalogItemId">Catalog item id (catalog_items.id).</param>
    /// <param name="versionId">Catalog version id (catalog_versions.id).</param>
    /// <param name="nestedSharedNames">
    /// Names of shared nested families declared by the .rfa. May be empty
    /// (when the family has no shared nested families or the extractor failed).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task ReplaceForVersionAsync(
        string catalogItemId,
        string versionId,
        IReadOnlyList<string> nestedSharedNames,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the list of shared nested family names for the catalog item's
    /// current version, in the order they were extracted (ordinal ASC).
    /// Returns an empty list if no names are stored (legacy catalog from
    /// before v2.0.0, or family has no shared nested families).
    /// </summary>
    /// <param name="catalogItemId">Catalog item id (catalog_items.id).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<string>> GetNamesForCurrentVersionAsync(
        string catalogItemId,
        CancellationToken ct = default);
}
