using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Stale;

public class StaleDetectorSnapshotTests
{
    private static StaleCheckResult Stale(string id, StaleReason r) =>
        new(id, $"Family-{id}", null, null, true, r);

    private static StaleCheckResult Fresh(string id) =>
        new(id, $"Family-{id}", null, null, false, StaleReason.None);

    [Fact]
    public void MergeInto_NullExisting_StartsEmpty()
    {
        var now = DateTimeOffset.UtcNow;
        var result = StaleSnapshotLogic.MergeInto(null, [Stale("a", StaleReason.NoEntityStorage)], now);
        Assert.Single(result.Results);
        Assert.True(result.Results["a"].IsStale);
    }

    [Fact]
    public void MergeInto_OtherCategories_ArePreserved()
    {
        // Snapshot has entry for "a" (in category A) and "b" (in category B).
        // New results contain "c" (in category C). "a" and "b" must remain.
        var now = DateTimeOffset.UtcNow;
        var existing = new FamilyStaleSnapshot(
            new Dictionary<string, StaleCheckResult>(StringComparer.Ordinal)
            {
                ["a"] = Stale("a", StaleReason.NoEntityStorage),
                ["b"] = Stale("b", StaleReason.VersionMismatch),
            },
            now);
        var merged = StaleSnapshotLogic.MergeInto(existing, [Stale("c", StaleReason.NoEntityStorage)], now);
        Assert.Equal(3, merged.Results.Count);
        Assert.True(merged.Results["a"].IsStale);
        Assert.True(merged.Results["b"].IsStale);
        Assert.True(merged.Results["c"].IsStale);
    }

    [Fact]
    public void MergeInto_SameId_NewResultWins()
    {
        // Re-checking a family should overwrite the stale entry with a fresh one.
        var now = DateTimeOffset.UtcNow;
        var existing = new FamilyStaleSnapshot(
            new Dictionary<string, StaleCheckResult>(StringComparer.Ordinal)
            {
                ["a"] = Stale("a", StaleReason.NoEntityStorage),
            },
            now);
        var merged = StaleSnapshotLogic.MergeInto(existing, [Fresh("a")], now);
        Assert.Single(merged.Results);
        Assert.False(merged.Results["a"].IsStale);
        Assert.Equal(StaleReason.None, merged.Results["a"].Reason);
    }

    [Fact]
    public void MergeInto_DoesNotMutateExisting()
    {
        // Defensive: the merge must not mutate the input snapshot.
        var now = DateTimeOffset.UtcNow;
        var existingDict = new Dictionary<string, StaleCheckResult>(StringComparer.Ordinal)
        {
            ["a"] = Stale("a", StaleReason.NoEntityStorage),
        };
        var existing = new FamilyStaleSnapshot(existingDict, now);
        var merged = StaleSnapshotLogic.MergeInto(existing, [Fresh("a"), Stale("b", StaleReason.NoEntityStorage)], now);
        Assert.True(existing.Results["a"].IsStale);
        Assert.Single(existing.Results);
        Assert.Equal(2, merged.Results.Count);
    }

    [Fact]
    public void RemoveFrom_NullExisting_ReturnsEmpty()
    {
        var result = StaleSnapshotLogic.RemoveFrom(null, ["a"], DateTimeOffset.UtcNow);
        Assert.Empty(result.Results);
    }

    [Fact]
    public void RemoveFrom_ExistingIds_AreDeleted()
    {
        // After a successful update of "a" and "b", the snapshot must keep "c"
        // (and any other entries that were not updated).
        var now = DateTimeOffset.UtcNow;
        var existing = new FamilyStaleSnapshot(
            new Dictionary<string, StaleCheckResult>(StringComparer.Ordinal)
            {
                ["a"] = Stale("a", StaleReason.NoEntityStorage),
                ["b"] = Stale("b", StaleReason.NoEntityStorage),
                ["c"] = Stale("c", StaleReason.VersionMismatch),
            },
            now);
        var updated = StaleSnapshotLogic.RemoveFrom(existing, ["a", "b"], now);
        Assert.Single(updated.Results);
        Assert.True(updated.Results.ContainsKey("c"));
        Assert.False(updated.Results.ContainsKey("a"));
    }

    [Fact]
    public void RemoveFrom_UnknownIds_NoChange()
    {
        // Removing IDs that are not in the snapshot must return the same instance
        // (no allocation, no churn).
        var now = DateTimeOffset.UtcNow;
        var existing = new FamilyStaleSnapshot(
            new Dictionary<string, StaleCheckResult>(StringComparer.Ordinal)
            {
                ["a"] = Stale("a", StaleReason.NoEntityStorage),
            },
            now);
        var updated = StaleSnapshotLogic.RemoveFrom(existing, ["x", "y"], now);
        Assert.Same(existing, updated);
    }

    [Fact]
    public void RemoveFrom_PartialMatch_ReturnsNewInstance()
    {
        var now = DateTimeOffset.UtcNow;
        var existing = new FamilyStaleSnapshot(
            new Dictionary<string, StaleCheckResult>(StringComparer.Ordinal)
            {
                ["a"] = Stale("a", StaleReason.NoEntityStorage),
                ["b"] = Stale("b", StaleReason.NoEntityStorage),
            },
            now);
        var updated = StaleSnapshotLogic.RemoveFrom(existing, ["a", "x"], now);
        Assert.NotSame(existing, updated);
        Assert.Single(updated.Results);
        Assert.True(updated.Results.ContainsKey("b"));
    }

    [Fact]
    public void EndToEnd_CheckCategoryThenCheckFamilyThenMarkUpdated_PreservesOtherCategories()
    {
        // Simulates the exact user scenario from the bug report:
        // 1. Check category A (2 families) -> snapshot = {a1, a2}
        // 2. Check category B (3 families) -> snapshot = {a1, a2, b1, b2, b3}
        // 3. MarkUpdated(b1, b2)        -> snapshot = {a1, a2, b3}
        // 4. CheckFamily(b3)            -> snapshot = {a1, a2, b3-fresh}
        var now = DateTimeOffset.UtcNow;

        var step1 = StaleSnapshotLogic.MergeInto(
            null,
            [Stale("a1", StaleReason.NoEntityStorage), Stale("a2", StaleReason.NoEntityStorage)],
            now);
        Assert.Equal(2, step1.Results.Count);

        var step2 = StaleSnapshotLogic.MergeInto(
            step1,
            [
                Stale("b1", StaleReason.VersionMismatch),
                Stale("b2", StaleReason.VersionMismatch),
                Stale("b3", StaleReason.NoEntityStorage),
            ],
            now);
        Assert.Equal(5, step2.Results.Count);
        // Both A and B families must be present.
        Assert.True(step2.Results["a1"].IsStale);
        Assert.True(step2.Results["a2"].IsStale);
        Assert.True(step2.Results["b1"].IsStale);
        Assert.True(step2.Results["b2"].IsStale);
        Assert.True(step2.Results["b3"].IsStale);

        var step3 = StaleSnapshotLogic.RemoveFrom(step2, ["b1", "b2"], now);
        Assert.Equal(3, step3.Results.Count);
        Assert.True(step3.Results.ContainsKey("a1"));
        Assert.True(step3.Results.ContainsKey("a2"));
        Assert.True(step3.Results.ContainsKey("b3"));
        Assert.False(step3.Results.ContainsKey("b1"));
        Assert.False(step3.Results.ContainsKey("b2"));

        var step4 = StaleSnapshotLogic.MergeInto(step3, [Fresh("b3")], now);
        Assert.Equal(3, step4.Results.Count);
        Assert.True(step4.Results["a1"].IsStale);
        Assert.True(step4.Results["a2"].IsStale);
        Assert.False(step4.Results["b3"].IsStale);
    }

    [Fact]
    public void FilterStaleBySubtree_OnlyReturnsFamiliesInSubtree()
    {
        // Reproduces the user-reported bug: clicking 'Update' on a 2-family
        // category was processing all 43 stale families. The filter must scope
        // the batch to the clicked subtree only.
        var allStaleIds = new List<string> { "a1", "a2", "b1", "b2", "b3", "u1", "u2" };
        var map = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            ["a1"] = new HashSet<string> { "root", "sub" },
            ["a2"] = new HashSet<string> { "root", "sub" },
            ["b1"] = new HashSet<string> { "otherCat" },
            ["b2"] = new HashSet<string> { "otherCat" },
            ["b3"] = new HashSet<string> { "otherCat" },
            ["u1"] = new HashSet<string> { "__no_category__" },
            ["u2"] = new HashSet<string> { "__no_category__" },
        };
        var subA = (IReadOnlyCollection<string>)new HashSet<string>(StringComparer.Ordinal) { "root", "sub" };

        var filtered = StaleSnapshotLogic.FilterStaleBySubtree(allStaleIds, map, subA);

        Assert.Equal(2, filtered.Count);
        Assert.Contains("a1", filtered);
        Assert.Contains("a2", filtered);
        Assert.DoesNotContain("b1", filtered);
        Assert.DoesNotContain("b2", filtered);
        Assert.DoesNotContain("b3", filtered);
        Assert.DoesNotContain("u1", filtered);
        Assert.DoesNotContain("u2", filtered);
    }

    [Fact]
    public void FilterStaleBySubtree_EmptySubtree_ReturnsEmpty()
    {
        var allStaleIds = new List<string> { "a1" };
        var map = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            ["a1"] = new HashSet<string> { "cat" },
        };
        var empty = (IReadOnlyCollection<string>)new HashSet<string>(StringComparer.Ordinal);

        var filtered = StaleSnapshotLogic.FilterStaleBySubtree(allStaleIds, map, empty);

        Assert.Empty(filtered);
    }

    [Fact]
    public void FilterStaleBySubtree_IdNotInMap_Excluded()
    {
        // Defensive: a stale id with no map entry (e.g. removed from tree
        // between the Check and the Update) must not be processed.
        var allStaleIds = new List<string> { "known", "unknown" };
        var map = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            ["known"] = new HashSet<string> { "sub" },
        };
        var sub = (IReadOnlyCollection<string>)new HashSet<string>(StringComparer.Ordinal) { "sub" };

        var filtered = StaleSnapshotLogic.FilterStaleBySubtree(allStaleIds, map, sub);

        Assert.Single(filtered);
        Assert.Equal("known", filtered[0]);
    }

    [Fact]
    public void FilterStaleBySubtree_UncategorizedSubtree_IncludesOnlyUncategorized()
    {
        // "__no_category__" subtree must include only families whose category
        // list contains "__no_category__". This is the synthetic ID for
        // uncategorized items.
        var allStaleIds = new List<string> { "u1", "a1" };
        var map = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
        {
            ["u1"] = new HashSet<string> { "__no_category__" },
            ["a1"] = new HashSet<string> { "realCat" },
        };
        var subUncat = (IReadOnlyCollection<string>)new HashSet<string>(StringComparer.Ordinal) { "__no_category__" };

        var filtered = StaleSnapshotLogic.FilterStaleBySubtree(allStaleIds, map, subUncat);

        Assert.Single(filtered);
        Assert.Equal("u1", filtered[0]);
    }
}
