using SmartCon.Core.Models;
using Xunit;

namespace SmartCon.Tests.Core.Models;

public sealed class PurgeOptionsTests
{
    [Fact]
    public void Default_AllFlags_True()
    {
        var options = new PurgeOptions();
        Assert.True(options.PurgeRvtLinks);
        Assert.True(options.PurgeCadImports);
        Assert.True(options.PurgeImages);
        Assert.True(options.PurgePointClouds);
        Assert.True(options.PurgeGroups);
        Assert.True(options.PurgeAssemblies);
        Assert.True(options.PurgeSpaces);
        Assert.True(options.PurgeRebar);
        Assert.True(options.PurgeFabricReinforcement);
        Assert.True(options.PurgeUnused);
    }

    [Fact]
    public void With_SingleFlagFalse_OnlyThatFlagChanges()
    {
        var options = new PurgeOptions() with { PurgeRvtLinks = false };
        Assert.False(options.PurgeRvtLinks);
        Assert.True(options.PurgeCadImports);
        Assert.True(options.PurgeImages);
    }
}
