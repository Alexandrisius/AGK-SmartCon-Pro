using SmartCon.Core.Models.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Models;

public sealed class ElementSummaryGroupTests
{
    [Fact]
    public void Constructor_OnlyKey_OmitsGroupIdentity()
    {
        var group = new ElementSummaryGroup { Key = "OST_Walls" };

        Assert.Null(group.Family);
        Assert.Null(group.TypeId);
        Assert.Null(group.Category);
    }

    [Fact]
    public void Constructor_TypeGroup_CarriesFamilyTypeAndCategory()
    {
        var group = new ElementSummaryGroup
        {
            Key = "Type A",
            Count = 3,
            Family = "Family A",
            TypeId = 123456,
            Category = "Walls"
        };

        Assert.Equal("Type A", group.Key);
        Assert.Equal(3, group.Count);
        Assert.Equal("Family A", group.Family);
        Assert.Equal(123456, group.TypeId);
        Assert.Equal("Walls", group.Category);
    }
}
