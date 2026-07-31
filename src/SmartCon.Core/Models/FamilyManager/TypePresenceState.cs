namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// #187: tri-state presence of a type node in the active project — drives
/// the dot indicator in the catalog tree (grey / blue / orange).
/// </summary>
public enum TypePresenceState
{
    /// <summary>Type is not present in the active project (grey dot).</summary>
    NotInProject,

    /// <summary>Type is present and up to date (blue dot).</summary>
    InProject,

    /// <summary>Type is present but outdated vs the catalog (orange dot).</summary>
    StaleInProject,
}
