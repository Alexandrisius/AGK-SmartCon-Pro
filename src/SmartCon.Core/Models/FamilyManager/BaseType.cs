namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Kind of FamilyManager database connection, governing scope of visibility
/// and automatic activation behaviour (see #119).
/// </summary>
/// <remarks>
/// <see cref="General"/> — current behaviour: available for any Revit project,
/// no automatic activation, switched only manually.
/// <see cref="Project"/> — bound to the Revit project file name through a
/// configurable template (see <see cref="ProjectBaseBinding"/>); automatically
/// activated when a matching project becomes active and loading of its families
/// into a non-matching project is blocked.
/// </remarks>
public enum BaseType
{
    /// <summary>Generic library available to any project (default for legacy connections).</summary>
    General = 0,

    /// <summary>Library scoped to a specific Revit project by file-name template.</summary>
    Project = 1
}