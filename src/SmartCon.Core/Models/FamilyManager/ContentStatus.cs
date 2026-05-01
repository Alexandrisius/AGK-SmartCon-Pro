namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Status of published family content in the catalog.
/// FM is a Published zone — all content is published by definition.
/// These statuses manage lifecycle after publication.
/// </summary>
public enum ContentStatus
{
    /// <summary>Active published content, available for loading into projects.</summary>
    Active = 0,

    /// <summary>Deprecated — has known issues, not recommended for new projects. Loading shows a warning.</summary>
    Deprecated = 1,

    /// <summary>Retired — removed from publication, not available for loading.</summary>
    Retired = 2
}
