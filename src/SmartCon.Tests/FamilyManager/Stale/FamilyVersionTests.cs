using SmartCon.Core.Models.FamilyManager;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Stale;

public class FamilyVersionTests
{
    [Fact]
    public void Empty_HasZeroSchemaVersion()
    {
        Assert.Equal(0, FamilyVersion.Empty.SchemaVersion);
        Assert.Equal(string.Empty, FamilyVersion.Empty.CatalogItemId);
        Assert.Equal(DateTimeOffset.MinValue, FamilyVersion.Empty.LoadedAtUtc);
    }

    [Fact]
    public void CurrentSchemaVersion_IsOne()
    {
        Assert.Equal(1, FamilyVersion.CurrentSchemaVersion);
    }

    [Fact]
    public void Equality_TwoRecordsWithSameData_AreEqual()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new FamilyVersion(1, "cat1", "v1", now, 2025);
        var b = new FamilyVersion(1, "cat1", "v1", now, 2025);
        Assert.Equal(a, b);
    }
}
