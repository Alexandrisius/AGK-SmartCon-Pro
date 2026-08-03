using Autodesk.Revit.DB;

namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Location of a system type (<c>ElementType</c>) inside a project document:
/// name + owning category + element id. Used by
/// <c>ISystemTypeFinder.CollectTypes</c> for batch stale-detection matching
/// (Issue #104). <see cref="ElementId"/> is a carrier only (I-09).
/// </summary>
/// <param name="TypeName">Type name (document content, case-preserved).</param>
/// <param name="CategoryOrdinal"><c>BuiltInCategory</c> ordinal of the type's
/// category. Matches <c>catalog_items.revit_category_id</c>.</param>
/// <param name="TypeId">Element id of the type in the document.</param>
/// <param name="FamilyName">Revit system family of the type (Issue #183) —
/// collected so stale detection can match by (family, name) instead of
/// name alone; <c>null</c> when the type reports no family name.</param>
/// <param name="FamilyKey">Locale-invariant family identity (Issue #190,
/// ADR-064) — always computed for system types; preferred over
/// <paramref name="FamilyName"/> for matching.</param>
public sealed record SystemTypeLocation(
    string TypeName,
    int CategoryOrdinal,
    ElementId TypeId,
    string? FamilyName = null,
    string? FamilyKey = null);
