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
    /// a category and/or a system family. Returns the first match; duplicates
    /// within the same category+family are impossible in Revit.
    /// </summary>
    /// <param name="familyName">Issue #183: system family of the type
    /// (e.g. "Conduit with Fittings" vs "Conduit without Fittings") — the
    /// identity of a system type is (family, name, category). When
    /// <c>null</c>, the family is not filtered (legacy behaviour: the first
    /// name match wins, a Warn is logged on duplicates).</param>
    /// <param name="familyKey">Issue #190 (ADR-064): locale-invariant family
    /// key (<see cref="SystemFamilyKeys"/>). When non-null/non-empty it is
    /// the PRIMARY filter and <paramref name="familyName"/> is ignored —
    /// the localized name cannot match across locales.</param>
    ElementId? FindTypeByName(
        Document doc, string typeName, int? categoryOrdinal, string? familyName = null, string? familyKey = null);

    /// <summary>
    /// Collect all system types of the given categories. Used by stale
    /// detection to match catalog item types against project content in one
    /// pass.
    /// </summary>
    IReadOnlyList<SystemTypeLocation> CollectTypes(
        Document doc, IReadOnlyCollection<int> categoryOrdinals);
}
