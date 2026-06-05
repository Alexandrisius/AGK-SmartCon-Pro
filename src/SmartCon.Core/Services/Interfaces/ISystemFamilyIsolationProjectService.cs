using Autodesk.Revit.DB;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Stage-isolation step: copies selected system-family types from a source project
/// into a fresh .rvt, places instances on a 2×2 m grid (Level 1), normalizes
/// diameters/widths to round metric values, and saves the result under
/// <c>%TEMP%\SmartCon\SystemFamilyLoadFromProject\&lt;GUID&gt;\&lt;name&gt;.rvt</c>.
///
/// This is the single source of truth for both ImportActiveFile (Project) and
/// ImportSystemFamily (Picker) flows. Replaces the legacy
/// <c>ISystemFamilyRevitOperations.CreateCleanProjectWithTypes</c> which did not
/// place instances and used a different (untracked) temp path.
/// </summary>
public interface ISystemFamilyIsolationProjectService
{
    /// <summary>
    /// Creates an isolated .rvt containing the specified types and one
    /// normalized instance per type. Source MUST be the active project.
    /// MUST be called from the Revit UI thread (inside an ExternalEvent).
    /// </summary>
    CreateCleanProjectResult CreateCleanProjectWithTypesAndInstances(
        Document sourceDoc,
        IReadOnlyList<string> typeUniqueIds,
        BuiltInCategory category,
        string displayName);
}
