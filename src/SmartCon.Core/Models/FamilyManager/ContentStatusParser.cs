using System;

namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Parses the <c>content_status</c> text column from the catalog database into
/// a <see cref="ContentStatus"/> value. Maps the legacy <c>Retired</c> string
/// to <see cref="ContentStatus.Deprecated"/> so that rows written by older
/// versions of the add-in remain usable after the UI was reduced to a
/// two-state Active/Deprecated model. See #109 and
/// <c>docs/known-workarounds.md</c> for the full rationale.
/// </summary>
public static class ContentStatusParser
{
    /// <summary>
    /// Parse a status string read from the database. The comparison is
    /// ordinal-ignore-case. <c>"Retired"</c> (any casing) maps to
    /// <see cref="ContentStatus.Deprecated"/>. Unknown strings fall back to
    /// <see cref="ContentStatus.Active"/> (the column DEFAULT) and are logged
    /// by the caller rather than throwing — a corrupt status cell must not
    /// crash the tree load.
    /// </summary>
    public static ContentStatus Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return ContentStatus.Active;

        if (string.Equals(value, "Retired", StringComparison.OrdinalIgnoreCase))
            return ContentStatus.Deprecated;

        if (Enum.TryParse<ContentStatus>(value, ignoreCase: true, out var parsed))
            return parsed;

        return ContentStatus.Active;
    }
}
