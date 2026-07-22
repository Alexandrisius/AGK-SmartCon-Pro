namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Why a catalog family is unavailable for loading into the current Revit
/// document. Drives the lock badge + grayed text in the catalog tree and the
/// tooltip text explaining the reason and the remedy.
/// </summary>
public enum FamilyUnavailableReason
{
    None = 0,
    Deprecated = 1,
    RevitVersion = 2,
    DeprecatedAndRevitVersion = 3,
}
