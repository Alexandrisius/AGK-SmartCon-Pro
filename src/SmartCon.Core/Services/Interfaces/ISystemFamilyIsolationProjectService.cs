using Autodesk.Revit.DB;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Stage-isolation step: copies selected system-family types from a source project
/// into a fresh .rvt, places instances on a 2×2 m grid (Level 1), normalizes
/// diameters/widths to round metric values, and saves the result directly
/// into managed storage at the supplied path.
///
/// v2.0.0: no temp staging — caller computes the managed path and passes it in.
/// </summary>
public interface ISystemFamilyIsolationProjectService
{
    /// <summary>
    /// Creates an isolated .rvt containing the specified types and one
    /// normalized instance per type, then SaveAs into the supplied managed
    /// storage path. Source MUST be the active project.
    /// MUST be called from the Revit UI thread (inside an ExternalEvent).
    /// The staged file is marked as a SmartCon reference mini-project
    /// (Issue #188) BEFORE SaveAs.
    /// </summary>
    /// <param name="catalogItemId">Owning catalog item for the mini-project
    /// marker when known (precomputed or existing id); null is allowed.</param>
    CreateCleanProjectResult CreateCleanProjectWithTypesAndInstances(
        Document sourceDoc,
        IReadOnlyList<string> typeUniqueIds,
        BuiltInCategory category,
        string displayName,
        string managedRvtPath,
        string? catalogItemId = null);
}
