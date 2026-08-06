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
public sealed record FamilyDependencyInfo(
    string ChildCatalogItemId,
    string Kind,
    string? PartName,
    int Ordinal);
