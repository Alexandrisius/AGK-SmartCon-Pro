using Autodesk.Revit.DB;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Locates system types (<c>ElementType</c>: PipeType, WallType, DuctType, …)
/// in a project document by name and category (Issue #104). Counterpart of
/// <see cref="IFamilyFinder"/> for system families: a system catalog item
/// (mini-project) owns N types, so matching requires the category to
/// disambiguate equal names across categories ("Стандартный" pipe vs wall).
/// </summary>
/// <remarks>
/// Implementations MUST be called on the Revit main thread only (I-01).
/// Category is carried as the <c>BuiltInCategory</c> ordinal
/// (<c>catalog_items.revit_category_id</c>) so Core stays free of Revit enum
/// comparisons; the implementation converts it to a category
/// <see cref="ElementId"/> via <c>ElementIdCompat.Create</c>.
/// </remarks>
public interface ISystemTypeFinder
{
    /// <summary>
    /// Find a system type by name (case-insensitive), optionally restricted to
    /// a category. Returns the first match; duplicates within the same
    /// category are impossible in Revit, duplicates across categories are
    /// filtered out by <paramref name="categoryOrdinal"/> — when it is
    /// <c>null</c> and several categories contain a type with this name, a
    /// <c>Warn</c> is logged and the first match wins.
    /// </summary>
    ElementId? FindTypeByName(Document doc, string typeName, int? categoryOrdinal);

    /// <summary>
    /// Collect all system types of the given categories. Used by stale
    /// detection to match catalog item types against project content in one
    /// pass.
    /// </summary>
    IReadOnlyList<SystemTypeLocation> CollectTypes(
        Document doc, IReadOnlyCollection<int> categoryOrdinals);
}
