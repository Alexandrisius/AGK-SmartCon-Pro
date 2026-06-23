namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Request passed from IFamilyLoadOptions.OnSharedFamilyFound callback into the UI layer
/// to ask the user how to handle a single shared nested family conflict.
/// </summary>
/// <param name="SharedFamilyName">
/// Name of the shared nested family. In Revit 2024.3+ this is the actual nested family
/// (per Revit API contract). In Revit 2023 / 2024 < 24.3.0.13 the Revit bug
/// REVIT-198137 passes <c>null</c> instead, in which case this value is resolved
/// from the catalog DB (extracted at import time) or, as a last resort,
/// a generic placeholder is generated.
/// </param>
/// <param name="IsFamilyInUse">
/// True if one or more instances of this family are placed in the project.
/// When true, overwriting may affect placed instances — UI should warn the user.
/// </param>
/// <param name="ParentFamilyName">
/// Name of the parent (host) family being loaded. Useful for the dialog caption.
/// </param>
/// <param name="IndexInBatch">
/// 1-based index of this conflict in the current <c>LoadFamily</c> invocation.
/// 1 when only one shared nested family conflicts.
/// </param>
/// <param name="TotalInBatch">
/// Total number of shared nested families extracted at import time for the parent.
/// Used to display "Shared nested X of N" in the dialog when more than one
/// conflict is expected. May be 0 when the catalog has no data (e.g. legacy
/// catalog from before v2.0.0); the dialog hides the progress row in that case.
/// </param>
/// <param name="NameSource">
/// Indicates where <paramref name="SharedFamilyName"/> came from. UI may show
/// a small badge like "name from SmartCon catalog" for transparency when
/// the name was not supplied by the Revit API.
/// </param>
public sealed record SharedFamilyDecisionRequest(
    string SharedFamilyName,
    bool IsFamilyInUse,
    string ParentFamilyName,
    int IndexInBatch = 1,
    int TotalInBatch = 1,
    SharedFamilyNameSource NameSource = SharedFamilyNameSource.RevitApi);
