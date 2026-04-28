using SmartCon.Core.Models.FamilyManager;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Models;

public sealed class FamilyContentStatusTests
{
    [Fact]
    public void Default_IsDraft()
    {
        FamilyContentStatus value = default;
        Assert.Equal(FamilyContentStatus.Draft, value);
    }

    [Fact]
    public void AllValues_AreDefined()
    {
        var values = Enum.GetValues<FamilyContentStatus>();
        Assert.Equal(4, values.Length);
        Assert.Contains(FamilyContentStatus.Draft, values);
        Assert.Contains(FamilyContentStatus.Verified, values);
        Assert.Contains(FamilyContentStatus.Deprecated, values);
        Assert.Contains(FamilyContentStatus.Archived, values);
    }

    [Fact]
    public void ToString_ReturnsName()
    {
        Assert.Equal("Draft", FamilyContentStatus.Draft.ToString());
        Assert.Equal("Verified", FamilyContentStatus.Verified.ToString());
        Assert.Equal("Deprecated", FamilyContentStatus.Deprecated.ToString());
        Assert.Equal("Archived", FamilyContentStatus.Archived.ToString());
    }
}
