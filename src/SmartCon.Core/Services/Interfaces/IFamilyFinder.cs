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
    /// Find a family element in the given document by its name.
    /// </summary>
    /// <param name="doc">Active Revit project document.</param>
    /// <param name="familyName">Display name of the family to locate.</param>
    /// <returns>The first matching <see cref="Autodesk.Revit.DB.Family"/>'s
    /// <see cref="ElementId"/>, or <c>null</c> if no family is loaded with that name.</returns>
    /// <remarks>
    /// <para>Matching is case-<strong>in</strong>sensitive. This is consistent with
    /// <c>IFamilySearchService</c> in the wider project and with how Revit
    /// itself displays family names in the Project Browser (case-preserving,
    /// case-insensitive match).</para>
    /// <para>If the project contains more than one <see cref="Autodesk.Revit.DB.Family"/>
    /// element with the same name (rare, but possible — duplicate loadable
    /// variants, system-family collisions), the first match is returned and a
    /// <c>Warn</c> is written to <c>smartcon.log</c> with the duplicate count
    /// so the operator can resolve the ambiguity. The caller SHOULD then
    /// trust the in-Revit state of the returned <see cref="ElementId"/> —
    /// writing the version marker to it will not affect the duplicate.</para>
    /// <para>Implementations MUST be safe to call on the Revit main thread only.
    /// Callers in async contexts should marshal via
    /// <see cref="IFamilyManagerAwaitableEvent.RaiseAsync(Action{object}, CancellationToken)"/>.</para>
    /// </remarks>
    ElementId? FindByName(Document doc, string familyName);

    /// <summary>
    /// Issue #187: collects every loaded family symbol as (family name, type
    /// name) pairs in ONE collector pass — the data source for the
    /// project-presence badges of loadable leaves and their type nodes
    /// (per-item <see cref="FindByName"/> calls would be O(N) scans).
    /// </summary>
    /// <remarks>Implementations MUST be called on the Revit main thread only.</remarks>
    IReadOnlyList<(string FamilyName, string TypeName)> CollectLoadedFamilySymbols(Document doc);
}
