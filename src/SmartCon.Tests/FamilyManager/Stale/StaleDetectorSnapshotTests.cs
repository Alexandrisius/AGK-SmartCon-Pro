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
}
