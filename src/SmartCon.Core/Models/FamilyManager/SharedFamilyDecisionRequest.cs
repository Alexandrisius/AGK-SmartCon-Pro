namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Request passed from IFamilyLoadOptions.OnSharedFamilyFound callback into the UI layer
/// to ask the user how to handle a single shared nested family conflict.
/// </summary>
/// <param name="SharedFamilyName">
/// Name of the shared nested family as reported by Revit.
/// In Revit 2024.3+ this is the actual nested family; in earlier versions
/// the Revit bug REVIT-198137 may pass the parent family name instead.
/// </param>
/// <param name="IsFamilyInUse">
/// True if one or more instances of this family are placed in the project.
/// When true, overwriting may affect placed instances — UI should warn the user.
/// </param>
/// <param name="ParentFamilyName">
/// Name of the parent (host) family being loaded. Useful for the dialog caption.
/// </param>
public sealed record SharedFamilyDecisionRequest(
    string SharedFamilyName,
    bool IsFamilyInUse,
    string ParentFamilyName);
