using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Pure merge / prune logic for the stale-detection snapshot (ADR-030, Phase 24).
/// Kept in Core so unit tests can verify the snapshot semantics without spinning
/// up the Revit API (which would require the RevitAPI assembly).
/// </summary>
public static class StaleSnapshotLogic
{
    /// <summary>
    /// Merge <paramref name="newResults"/> into the existing snapshot. Existing
    /// entries for OTHER catalog item IDs are preserved. Entries that share a
    /// catalog item ID with the new results are overwritten (a re-check wins
    /// over a previous stale marker).
    /// </summary>
    public static FamilyStaleSnapshot MergeInto(
        FamilyStaleSnapshot? existing,
        IReadOnlyList<StaleCheckResult> newResults,
        DateTimeOffset now)
    {
        var dict = new Dictionary<string, StaleCheckResult>(StringComparer.Ordinal);
        if (existing is not null)
        {
            foreach (var kvp in existing.Results) dict[kvp.Key] = kvp.Value;
        }
        foreach (var r in newResults)
        {
            dict[r.CatalogItemId] = r;
        }
        return new FamilyStaleSnapshot(dict, now);
    }

    /// <summary>
    /// Remove the given catalog item IDs from the existing snapshot. Returns the
    /// same snapshot instance if no entries matched (avoids allocation when the
    /// caller asked to remove IDs that were not in the snapshot).
    /// </summary>
    public static FamilyStaleSnapshot RemoveFrom(
        FamilyStaleSnapshot? existing,
        IReadOnlyCollection<string> catalogItemIds,
        DateTimeOffset now)
    {
        if (existing is null) return FamilyStaleSnapshot.Empty;
        var dict = new Dictionary<string, StaleCheckResult>(StringComparer.Ordinal);
        foreach (var kvp in existing.Results) dict[kvp.Key] = kvp.Value;
        var removed = 0;
        foreach (var id in catalogItemIds)
        {
            if (dict.Remove(id)) removed++;
        }
        if (removed == 0) return existing;
        return new FamilyStaleSnapshot(dict, now);
    }
}
