using SmartCon.Core.Models.FamilyManager;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Stale;

public class StaleSnapshotAdditionalTests
{
    private static StaleCheckResult StaleResult(string id, StaleReason r) =>
        new(id, $"Family-{id}", null, null, true, r);

    private static StaleCheckResult FreshResult(string id) =>
        new(id, $"Family-{id}", null, null, false, StaleReason.None);

    [Fact]
    public void FamilyStaleSnapshot_Constructor_NullResults_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new FamilyStaleSnapshot(null!, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void FamilyStaleSnapshot_Empty_HasMinValueTimestamp()
    {
        Assert.Equal(DateTimeOffset.MinValue, FamilyStaleSnapshot.Empty.CheckedAtUtc);
        Assert.Empty(FamilyStaleSnapshot.Empty.Results);
    }

    [Fact]
    public void FamilyStaleSnapshot_Equality_SameContent_AreEqual()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new FamilyStaleSnapshot(
            new Dictionary<string, StaleCheckResult>(StringComparer.Ordinal) { ["x"] = StaleResult("x", StaleReason.NoEntityStorage) },
            now);
        var b = new FamilyStaleSnapshot(
            new Dictionary<string, StaleCheckResult>(StringComparer.Ordinal) { ["x"] = StaleResult("x", StaleReason.NoEntityStorage) },
            now);

        Assert.Equal(a, b);
    }

    [Fact]
    public void FamilyStaleSnapshot_Equality_DifferentTimestamp_NotEqual()
    {
        var a = new FamilyStaleSnapshot(
            new Dictionary<string, StaleCheckResult>(StringComparer.Ordinal) { ["x"] = StaleResult("x", StaleReason.NoEntityStorage) },
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var b = new FamilyStaleSnapshot(
            new Dictionary<string, StaleCheckResult>(StringComparer.Ordinal) { ["x"] = StaleResult("x", StaleReason.NoEntityStorage) },
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void FamilyStaleSnapshot_Equality_DifferentCount_NotEqual()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new FamilyStaleSnapshot(
            new Dictionary<string, StaleCheckResult>(StringComparer.Ordinal)
            {
                ["x"] = StaleResult("x", StaleReason.NoEntityStorage),
                ["y"] = StaleResult("y", StaleReason.NoEntityStorage),
            },
            now);
        var b = new FamilyStaleSnapshot(
            new Dictionary<string, StaleCheckResult>(StringComparer.Ordinal)
            {
                ["x"] = StaleResult("x", StaleReason.NoEntityStorage),
            },
            now);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void FamilyStaleSnapshot_Equality_NullOther_NotEqual()
    {
        var a = new FamilyStaleSnapshot(
            new Dictionary<string, StaleCheckResult>(StringComparer.Ordinal), DateTimeOffset.UtcNow);
        Assert.False(a.Equals(null));
    }

    [Fact]
    public void FamilyStaleSnapshot_Equality_SameInstance_AreEqual()
    {
        var a = new FamilyStaleSnapshot(
            new Dictionary<string, StaleCheckResult>(StringComparer.Ordinal), DateTimeOffset.UtcNow);
        Assert.True(a.Equals(a));
    }

    [Fact]
    public void FamilyStaleSnapshot_GetHashCode_SameContent_HashEqual()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new FamilyStaleSnapshot(
            new Dictionary<string, StaleCheckResult>(StringComparer.Ordinal) { ["x"] = StaleResult("x", StaleReason.NoEntityStorage) },
            now);
        var b = new FamilyStaleSnapshot(
            new Dictionary<string, StaleCheckResult>(StringComparer.Ordinal) { ["x"] = StaleResult("x", StaleReason.NoEntityStorage) },
            now);

        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void StaleCheckResult_RecordEquality_SameData_AreEqual()
    {
        var a = new StaleCheckResult("x", "Family", "v2", "v1", true, StaleReason.VersionMismatch);
        var b = new StaleCheckResult("x", "Family", "v2", "v1", true, StaleReason.VersionMismatch);
        Assert.Equal(a, b);
    }

    [Fact]
    public void StaleCheckResult_RecordEquality_DifferentStale_NotEqual()
    {
        var a = new StaleCheckResult("x", "Family", "v2", "v1", true, StaleReason.VersionMismatch);
        var b = new StaleCheckResult("x", "Family", "v2", "v1", false, StaleReason.None);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void StaleUpdateRequest_Default_RecursiveIsTrue()
    {
        var req = new StaleUpdateRequest(["a"], OverwriteParameterValues: false);
        Assert.True(req.Recursive);
        Assert.False(req.OverwriteParameterValues);
        Assert.Equal(new[] { "a" }, req.CatalogItemIds);
    }

    [Fact]
    public void StaleUpdateRequest_ExplicitRecursive_FalsePropagates()
    {
        var req = new StaleUpdateRequest(["a"], OverwriteParameterValues: true, Recursive: false);
        Assert.False(req.Recursive);
    }

    [Fact]
    public void StaleBatchUpdateResult_Invariant_HoldsForCancelledBatch()
    {
        var result = new StaleBatchUpdateResult(
            TotalRequested: 5,
            SuccessCount: 2,
            FailedCount: 1,
            SkippedCount: 2,
            SuccessCatalogItemIds: ["a", "b"],
            FailedCatalogItemIds: ["c"]);

        Assert.Equal(result.TotalRequested, result.SuccessCount + result.FailedCount + result.SkippedCount);
    }

    [Fact]
    public void StaleBatchUpdateResult_Invariant_HoldsForFullSuccess()
    {
        var result = new StaleBatchUpdateResult(
            TotalRequested: 3,
            SuccessCount: 3,
            FailedCount: 0,
            SkippedCount: 0,
            SuccessCatalogItemIds: ["a", "b", "c"],
            FailedCatalogItemIds: []);

        Assert.Equal(result.TotalRequested, result.SuccessCount + result.FailedCount + result.SkippedCount);
    }

    [Fact]
    public void StaleBatchUpdateResult_RecordEquality_SamePrimitiveData_AreEqual()
    {
        // Note: record equality compares IReadOnlyList<T> by reference, not by
        // value (the BCL default). Two distinct list instances with the same
        // content compare as NotEqual. The fields that ARE compared by value
        // (int, bool) must be equal.
        var a = new StaleBatchUpdateResult(3, 2, 1, 0, ["x", "y"], ["z"]);
        var b = new StaleBatchUpdateResult(3, 2, 1, 0, ["x", "y"], ["z"]);
        Assert.Equal(a.TotalRequested, b.TotalRequested);
        Assert.Equal(a.SuccessCount, b.SuccessCount);
        Assert.Equal(a.FailedCount, b.FailedCount);
        Assert.Equal(a.SkippedCount, b.SkippedCount);
    }

    [Fact]
    public void StaleBatchUpdateResult_DifferentPrimitiveData_NotEqual()
    {
        var a = new StaleBatchUpdateResult(3, 2, 1, 0, ["x"], ["y"]);
        var b = new StaleBatchUpdateResult(4, 2, 1, 0, ["x"], ["y"]);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void StaleBatchUpdateProgress_RecordEquality_AllFieldsCompared()
    {
        var a = new StaleBatchUpdateProgress(2, 5, "Family");
        var b = new StaleBatchUpdateProgress(2, 5, "Family");
        var c = new StaleBatchUpdateProgress(2, 5, "Different");
        var d = new StaleBatchUpdateProgress(3, 5, "Family");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, d);
    }

    [Fact]
    public void CategoryStaleStats_Empty_HasFalseAndZero()
    {
        Assert.False(CategoryStaleStats.Empty.HasStale);
        Assert.Equal(0, CategoryStaleStats.Empty.StaleCount);
    }

    [Fact]
    public void CategoryStaleStats_RecordEquality_SameData_AreEqual()
    {
        var a = new CategoryStaleStats(true, 5);
        var b = new CategoryStaleStats(true, 5);
        var c = new CategoryStaleStats(false, 5);
        var d = new CategoryStaleStats(true, 6);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, d);
    }

    [Fact]
    public void StaleReason_Enum_HasExpectedValues()
    {
        Assert.Equal(0, (int)StaleReason.None);
        Assert.Equal(1, (int)StaleReason.NoEntityStorage);
        Assert.Equal(2, (int)StaleReason.VersionMismatch);
        Assert.Equal(3, (int)StaleReason.RevitVersionMismatch);
        Assert.Equal(4, (int)StaleReason.NotInCatalog);
    }
}
