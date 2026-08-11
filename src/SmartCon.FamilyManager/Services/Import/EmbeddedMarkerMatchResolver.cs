using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.FamilyManager.Services.Import;

/// <summary>
/// #209 (2026-08-11): version resolution override for nested-child import
/// rows. The embedded copy of a shared nested family carries an ES version
/// marker written by the stale-update command ONLY after the embedded
/// content passed FHV8V verification (see StaleFamilyUpdater) — that is a
/// strictly stronger version signal than identity-hash matching, because a
/// reload merge NEVER propagates parameter groups, so the embedded
/// identity hash of a correctly updated family keeps matching the OLD
/// stored version (or nothing) forever. Pure decision logic — unit-tested;
/// the caller falls back to the plain dedup result when this returns null.
/// </summary>
internal static class EmbeddedMarkerMatchResolver
{
    /// <summary>
    /// Returns the override (Duplicate + marker label) only when the marker
    /// points at the SAME catalog item the dedup name-lookup resolved and
    /// the marker label differs from the hash-matched label. A null result
    /// means "no override — keep the dedup result as-is" (no marker, marker
    /// of a differently-named/unknown item, or the match already agrees).
    /// </summary>
    public static (FamilyBatchImportStatus Status, string MatchedVersionLabel)? ResolveOverride(
        string? markerCatalogItemId,
        string? markerVersionLabel,
        string? dedupExistingCatalogItemId,
        string? dedupMatchedVersionLabel)
    {
        if (string.IsNullOrEmpty(markerCatalogItemId) || string.IsNullOrEmpty(markerVersionLabel))
        {
            return null;
        }

        if (!string.Equals(dedupExistingCatalogItemId, markerCatalogItemId, StringComparison.Ordinal))
        {
            return null;
        }

        if (string.Equals(dedupMatchedVersionLabel, markerVersionLabel, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return (FamilyBatchImportStatus.Duplicate, markerVersionLabel!);
    }
}
