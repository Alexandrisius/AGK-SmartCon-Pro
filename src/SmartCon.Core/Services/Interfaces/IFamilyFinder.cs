using Autodesk.Revit.DB;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Locates a Revit <see cref="Autodesk.Revit.DB.Family"/> element in a project document
/// by name. This is the only Revit-API call that the FamilyManager UI layer needs
/// directly (everything else is hidden behind other services).
/// </summary>
public interface IFamilyFinder
{
    /// <summary>
    /// Find a family element in the given document by its name (exact match, ordinal).
    /// Returns <c>null</c> if no family is loaded with that name.
    /// </summary>
    /// <remarks>
    /// Implementations MUST be safe to call on the Revit main thread only.
    /// Callers in async contexts should marshal via <c>IRevitAwaitable.RaiseAsync</c>.
    /// </remarks>
    ElementId? FindByName(Document doc, string familyName);
}
