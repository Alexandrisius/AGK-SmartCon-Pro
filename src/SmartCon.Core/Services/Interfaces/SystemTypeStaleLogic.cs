using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Pure stale-verdict logic for system catalog items (Issue #104). A system
/// item (mini-project) owns N types, each carrying its own ES marker in the
/// project; this class aggregates the per-type markers into one item-level
/// verdict. Contains no Revit API references so it is unit-testable in
/// isolation (same pattern as <see cref="StaleSnapshotLogic"/>).
/// </summary>
public static class SystemTypeStaleLogic
{
    /// <summary>
    /// Compute the stale reason of a single marker against catalog state.
    /// Mirrors the loadable-family rules: foreign catalog id or a different
    /// version label → <see cref="StaleReason.VersionMismatch"/>; a different
    /// source Revit version → <see cref="StaleReason.RevitVersionMismatch"/>.
    /// </summary>
    public static StaleReason ComputeReason(
        FamilyVersion marker,
        string catalogItemId,
        string? currentVersionLabel,
        int targetRevit)
    {
        if (!string.IsNullOrEmpty(marker.CatalogItemId) &&
            !string.Equals(marker.CatalogItemId, catalogItemId, StringComparison.Ordinal))
        {
            return StaleReason.VersionMismatch;
        }

        if (!string.IsNullOrEmpty(currentVersionLabel) &&
            !string.Equals(marker.VersionLabel, currentVersionLabel, StringComparison.Ordinal))
        {
            return StaleReason.VersionMismatch;
        }
        if (marker.SourceRevitVersion > 0 && targetRevit > 0 &&
            marker.SourceRevitVersion != targetRevit)
        {
            return StaleReason.RevitVersionMismatch;
        }
        return StaleReason.None;
    }

    /// <summary>
    /// #253: refines a per-type <see cref="StaleReason.VersionMismatch"/>
    /// verdict of a SYSTEM type from the catalog DB alone — the type's
    /// per-type content hash at the marker's version vs the current
    /// version, plus the shared-section rule (a changed section outside
    /// VALUES affects every type). Mirrors the loadable DB map
    /// (<c>StaleDetector.ComputeDbPerTypeStaleAsync</c>): a type whose
    /// content is identical between the two versions is NOT stale even
    /// though its marker is older — updating it would be a no-op.
    /// <c>null</c> when the analytics are incomplete for THIS type (the
    /// caller keeps the marker-based verdict); the family/item-level
    /// verdict is never refined — only the per-type dots.
    /// </summary>
    public static bool? RefineVersionMismatchWithContent(
        IReadOnlyList<FamilyTypeHashEntry>? fromVersionTypes,
        IReadOnlyList<FamilyTypeHashEntry>? currentVersionTypes,
        IReadOnlyList<string> sharedChangedSections,
        string typeIdentityKey)
    {
        if (fromVersionTypes is null || currentVersionTypes is null)
        {
            return null;
        }

        var from = fromVersionTypes.FirstOrDefault(
            e => string.Equals(e.TypeIdentityKey, typeIdentityKey, StringComparison.OrdinalIgnoreCase));
        if (from is null)
        {
            // No hash row for THIS type at the marker's version — the
            // analytics cannot prove anything about it (backfill pending /
            // legacy row), so the marker verdict stands.
            return null;
        }

        if (sharedChangedSections.Count > 0)
        {
            return true;
        }

        var to = currentVersionTypes.FirstOrDefault(
            e => string.Equals(e.TypeIdentityKey, typeIdentityKey, StringComparison.OrdinalIgnoreCase));
        if (to is null)
        {
            // The type was REMOVED in the current version — the project's
            // copy is outdated by definition (same rule as the loadable map).
            return true;
        }

        return !string.Equals(from.HashHex, to.HashHex, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Aggregate per-type markers of one system catalog item into a single
    /// verdict. The item is stale when ANY of its project-loaded types has
    /// a MISMATCHED marker (first non-None reason wins, in type order).
    /// <para>
    /// Stress test 2026-08-05 (semantics change): a project-loaded type with
    /// NO marker is NOT stale — template-native types (both conduit
    /// «Короб» types exist in every Revit template) and types copied
    /// outside the catalog simply have unknown provenance. Marking them
    /// stale fired a false badge immediately after a successful DnD of a
    /// single sibling type. Only a marker that PROVES catalog origin can
    /// also prove outdatedness.
    /// </para>
    /// <paramref name="markers"/> must contain only types that exist in the
    /// project — the caller filters unloaded types out beforehand.
    /// </summary>
    public static (bool IsStale, StaleReason Reason, string? LoadedLabel) Aggregate(
        IReadOnlyList<FamilyVersion?> markers,
        string catalogItemId,
        string? currentVersionLabel,
        int targetRevit)
    {
        string? loadedLabel = null;
        foreach (var marker in markers)
        {
            if (marker is null)
            {
                // No marker = unknown provenance (template-native / copied
                // bypassing the catalog) — see the class remarks; NOT stale.
                continue;
            }

            loadedLabel ??= marker.VersionLabel;
            var reason = ComputeReason(marker, catalogItemId, currentVersionLabel, targetRevit);
            if (reason != StaleReason.None)
            {
                return (true, reason, marker.VersionLabel);
            }
        }
        return (false, StaleReason.None, loadedLabel);
    }
}
