namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// One incoming dependency reference onto a catalog item (E5, #213,
/// ADR-067): which parent item — and which of its versions — declares the
/// item as a dependency in <c>family_dependencies</c>. Used by the
/// dependency guard (a referenced item cannot be deleted while ANY version
/// of ANY parent references it) and by the tree paperclip indicator.
/// </summary>
/// <param name="ParentCatalogItemId">Referencing parent catalog item id.</param>
/// <param name="ParentName">Parent display name (catalog_items.name).</param>
/// <param name="VersionLabel">Parent version that declares the link.</param>
/// <param name="IsCurrentVersion">
/// Whether <paramref name="VersionLabel"/> is the parent's current active
/// version. Guard semantics ignore this (archived versions block too) — the
/// flag exists for the UI list («v2 — активная»).
/// </param>
public sealed record FamilyDependencyReference(
    string ParentCatalogItemId,
    string ParentName,
    string VersionLabel,
    bool IsCurrentVersion);
