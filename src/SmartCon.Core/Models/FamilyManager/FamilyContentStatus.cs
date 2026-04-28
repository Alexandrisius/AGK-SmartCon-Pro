namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Status of a family catalog item.
/// </summary>
public enum FamilyContentStatus
{
    /// <summary>Just imported, not yet verified.</summary>
    Draft = 0,
    /// <summary>Verified and approved for use.</summary>
    Verified = 1,
    /// <summary>Deprecated, should not be used in new projects.</summary>
    Deprecated = 2,
    /// <summary>Archived, hidden from default search.</summary>
    Archived = 3
}
