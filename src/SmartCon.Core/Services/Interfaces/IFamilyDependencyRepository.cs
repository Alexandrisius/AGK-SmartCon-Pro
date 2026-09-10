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

    /// <summary>
    /// Reverse lookup (E5, #213, ADR-067): every (parent item, parent
    /// version) pair that references <paramref name="childCatalogItemId"/>
    /// — across ALL versions, not just current ones (an archived parent
    /// version blocks the child's deletion exactly like the active one;
    /// unified rule for routing and shared_nested links). Multiple links of
    /// the same parent version (different kinds/parts) collapse into one
    /// reference. Empty list = the item is free.
    /// </summary>
    Task<IReadOnlyList<FamilyDependencyReference>> GetReferencingParentsAsync(
        string childCatalogItemId,
        CancellationToken ct = default);

    /// <summary>
    /// Batch variant of <see cref="GetReferencingParentsAsync"/> — one query
    /// for the whole tree (paperclip indicator). The dictionary contains an
    /// entry ONLY for children that have at least one incoming reference.
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<FamilyDependencyReference>>> GetReferencingParentsBatchAsync(
        IReadOnlyCollection<string> childCatalogItemIds,
        CancellationToken ct = default);

    /// <summary>
    /// Dependency drift lookup (E2, #209, schema V30): for the given parent
    /// items returns the links of their CURRENT versions whose embedded
    /// child version (<c>child_version_label</c>) differs from the child's
    /// current active version — i.e. a newer child version was activated
    /// after the parent embedded an older copy. Links with unknown embedded
    /// version (NULL, legacy V29) never report drift. The dictionary
    /// contains an entry ONLY for parents with at least one drifted link;
    /// the tree badge and the load-time block both read from this.
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<FamilyDependencyDrift>>> GetDependencyDriftBatchAsync(
        IReadOnlyCollection<string> parentCatalogItemIds,
        CancellationToken ct = default);
}
