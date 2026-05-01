using SmartCon.Core.Models.FamilyManager;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Models;

public sealed class ContentStatusTests
{
    [Fact]
    public void Default_IsActive()
    {
        ContentStatus value = default;
        Assert.Equal(ContentStatus.Active, value);
    }

    [Fact]
    public void AllValues_AreDefined()
    {
        var values = (ContentStatus[])Enum.GetValues(typeof(ContentStatus));
        Assert.Equal(3, values.Length);
        Assert.Contains(ContentStatus.Active, values);
        Assert.Contains(ContentStatus.Deprecated, values);
        Assert.Contains(ContentStatus.Retired, values);
    }

    [Fact]
    public void ToString_ReturnsName()
    {
        Assert.Equal("Active", ContentStatus.Active.ToString());
        Assert.Equal("Deprecated", ContentStatus.Deprecated.ToString());
        Assert.Equal("Retired", ContentStatus.Retired.ToString());
    }
}
