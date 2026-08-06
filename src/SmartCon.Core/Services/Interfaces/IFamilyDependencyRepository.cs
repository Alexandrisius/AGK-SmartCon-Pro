using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Persists parent→child dependency links between catalog items
/// (<c>family_dependencies</c>, schema V29, ADR-066, EPIC #207). Populated at
/// import time by the batch-import pipeline (routing fittings of system
/// categories in E1; shared nested families in E2) and consumed at sync time
/// by <c>IFittingDependencyResolver</c> to resolve a routing rule to the
/// child's catalog item by link instead of by fragile name lookup.
/// </summary>
public interface IFamilyDependencyRepository
{
    /// <summary>
    /// Replaces the entire dependency list declared by one parent version.
    /// Any previously stored links for <paramref name="parentVersionId"/> are
    /// removed. Duplicate (child, kind) pairs are deduplicated before insert.
    /// </summary>
    /// <param name="parentCatalogItemId">Parent catalog item id.</param>
    /// <param name="parentVersionId">
    /// Parent catalog version id (catalog_versions.id) that declared the
    /// dependencies — the staging/active version at import time.
    /// </param>
    /// <param name="dependencies">Links to persist; may be empty.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ReplaceForVersionAsync(
        string parentCatalogItemId,
        string parentVersionId,
        IReadOnlyList<FamilyDependencyInfo> dependencies,
        CancellationToken ct = default);

    /// <summary>
    /// Same as <see cref="ReplaceForVersionAsync"/>, but resolves the parent
    /// version id from the parent's CURRENT active version
    /// (<c>catalog_items.current_version_label</c>). This is the Phase-3
    /// batch-import path: the executor knows the parent item was just
    /// (re)imported and attaches the links to whatever version became
    /// current. Returns the number of links written; 0 (with a Warn log)
    /// when the parent has no current version.
    /// </summary>
    /// <param name="parentCatalogItemId">Parent catalog item id.</param>
    /// <param name="dependencies">Links to persist; may be empty.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<int> ReplaceForCurrentVersionAsync(
        string parentCatalogItemId,
        IReadOnlyList<FamilyDependencyInfo> dependencies,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the dependency links declared by the parent's CURRENT active
    /// version (joined via <c>catalog_items.current_version_label</c>),
    /// ordered by <see cref="FamilyDependencyInfo.Ordinal"/>. Returns an
    /// empty list when the parent has no recorded dependencies (e.g. staged
    /// before E1 — the sync path falls back to name-based resolution).
    /// </summary>
    /// <param name="parentCatalogItemId">Parent catalog item id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<FamilyDependencyInfo>> GetForCurrentVersionAsync(
        string parentCatalogItemId,
        CancellationToken ct = default);
}
