namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Status of published family content in the catalog.
/// FM is a Published zone — all content is published by definition.
/// These statuses manage lifecycle after publication.
/// </summary>
/// <remarks>
/// Only <see cref="Active"/> and <see cref="Deprecated"/> are surfaced in the
/// UI (Properties dialog ComboBox). <see cref="Retired"/> is a legacy value
/// from a previous 3-state model; existing DB rows with "Retired" are mapped
/// to <see cref="Deprecated"/> on read by <see cref="ContentStatusParser"/>.
/// New records can no longer be assigned Retired. See #109 for rationale.
/// </remarks>
public enum ContentStatus
{
    /// <summary>Active (current) content — available for loading into projects.</summary>
    Active = 0,

    /// <summary>Deprecated (not current) content — loading is blocked until the
    /// owner publishes an updated version. Use this when a family is superseded
    /// or known to have issues and should not be placed into new projects.</summary>
    Deprecated = 1,

    /// <summary>Legacy status preserved for backward compatibility with existing
    /// DB rows only. Not selectable in the UI. Read paths map Retired → Deprecated
    /// via <see cref="ContentStatusParser"/>. Do not assign this value to new records.</summary>
    Retired = 2
}
