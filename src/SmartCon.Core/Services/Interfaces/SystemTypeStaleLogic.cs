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
    /// Aggregate per-type markers of one system catalog item into a single
    /// verdict. The item is stale when ANY of its project-loaded types has
    /// no marker (<see cref="StaleReason.NoEntityStorage"/>) or a mismatched
    /// marker (first non-None reason wins, in type order).
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
                return (true, StaleReason.NoEntityStorage, loadedLabel);
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
