using SmartCon.Core.Models.FamilyManager;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Models;

/// <summary>
/// Value-object tests for the system-type sync result records (Issue #104).
/// </summary>
public sealed class SystemFamilySyncResultTests
{
    private static SystemTypeSyncResult Ok(string name) =>
        new(name, SystemTypeSyncStatus.Updated, 10, 2);

    [Fact]
    public void AllSucceeded_EmptyResults_False()
    {
        var result = new SystemFamilySyncResult("item-1", []);
        Assert.False(result.AllSucceeded);
        Assert.Equal(0, result.SuccessCount);
        Assert.Equal(0, result.FailedCount);
    }

    [Fact]
    public void AllSucceeded_AllSuccess_True()
    {
        var result = new SystemFamilySyncResult("item-1",
        [
            Ok("a"),
            new SystemTypeSyncResult("b", SystemTypeSyncStatus.Created, 5, 0),
        ]);

        Assert.True(result.AllSucceeded);
        Assert.Equal(2, result.SuccessCount);
        Assert.Equal(0, result.FailedCount);
    }

    [Fact]
    public void AllSucceeded_OneFailure_False()
    {
        var result = new SystemFamilySyncResult("item-1",
        [
            Ok("a"),
            new SystemTypeSyncResult("b", SystemTypeSyncStatus.Failed, 0, 0, "boom"),
        ]);

        Assert.False(result.AllSucceeded);
        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(1, result.FailedCount);
    }

    [Theory]
    [InlineData(SystemTypeSyncStatus.Created, true)]
    [InlineData(SystemTypeSyncStatus.Updated, true)]
    [InlineData(SystemTypeSyncStatus.NotFoundInSource, false)]
    [InlineData(SystemTypeSyncStatus.NoPrototypeType, false)]
    [InlineData(SystemTypeSyncStatus.Failed, false)]
    public void IsSuccess_MapsFromStatus(SystemTypeSyncStatus status, bool expected)
    {
        var result = new SystemTypeSyncResult("t", status, 0, 0);
        Assert.Equal(expected, result.IsSuccess);
    }
}
