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
public sealed record SystemTypeLocation(
    string TypeName,
    int CategoryOrdinal,
    ElementId TypeId);
