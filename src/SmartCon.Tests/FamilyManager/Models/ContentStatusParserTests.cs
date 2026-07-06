using SmartCon.Core.Models.FamilyManager;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Models;

public sealed class ContentStatusParserTests
{
    [Theory]
    [InlineData("Active", ContentStatus.Active)]
    [InlineData("active", ContentStatus.Active)]
    [InlineData("ACTIVE", ContentStatus.Active)]
    [InlineData("Deprecated", ContentStatus.Deprecated)]
    [InlineData("deprecated", ContentStatus.Deprecated)]
    [InlineData("DEPRECATED", ContentStatus.Deprecated)]
    public void Parse_KnownStatuses_ReturnsExpected(string raw, ContentStatus expected)
    {
        Assert.Equal(expected, ContentStatusParser.Parse(raw));
    }

    [Theory]
    [InlineData("Retired")]
    [InlineData("retired")]
    [InlineData("RETIRED")]
    public void Parse_LegacyRetired_MapsToDeprecated(string raw)
    {
        Assert.Equal(ContentStatus.Deprecated, ContentStatusParser.Parse(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NullOrWhitespace_ReturnsActive(string? raw)
    {
        Assert.Equal(ContentStatus.Active, ContentStatusParser.Parse(raw));
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("Deleted")]
    [InlineData("Pending")]
    public void Parse_UnknownString_ReturnsActive(string raw)
    {
        Assert.Equal(ContentStatus.Active, ContentStatusParser.Parse(raw));
    }
}
