namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// One parent→child dependency link between catalog items (ADR-066,
/// <c>family_dependencies</c> table, schema V29). The link is version-scoped
/// on the PARENT side for history/audit: it records which staging version of
/// the parent declared the dependency. Version SELECTION at sync time never
/// uses the stored ids — the child is always loaded at its active version
/// (<c>IFamilyFileResolver.ResolveForLoadAsync</c>).
/// </summary>
/// <param name="ChildCatalogItemId">Child catalog item id (catalog_items.id).</param>
/// <param name="Kind">Dependency class — see <see cref="FamilyDependencyKind"/>.</param>
/// <param name="PartName">
/// Original routing-rule token "Family:Type" for
/// <see cref="FamilyDependencyKind.Routing"/> links (used to match a live
/// routing rule to its link without re-parsing the parent snapshot);
/// <c>null</c> for other kinds.
/// </param>
/// <param name="Ordinal">Stable ordering within the parent's dependency list.</param>
/// <param name="ChildVersionLabel">
/// E2 (#209, schema V30): version label of the child that was EMBEDDED in
/// the parent's version at import time (the batch row's
/// <c>PrecomputedVersionLabel</c> for imported children, the
/// hash-<c>MatchedVersionLabel</c> for dedup-linked duplicates). Compared
/// against the child's <c>current_version_label</c> to detect dependency
/// drift (a newer child version was activated after the parent embedded an
/// older copy) — a pure SQL check. <c>null</c> = unknown (legacy V29 links,
/// or a skipped child whose content matches no stored version) — such
/// links never raise drift.
/// </param>
public sealed record FamilyDependencyInfo(
    string ChildCatalogItemId,
    string Kind,
    string? PartName,
    int Ordinal,
    string? ChildVersionLabel = null);
