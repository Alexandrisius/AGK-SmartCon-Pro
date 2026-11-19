using SmartCon.Core.Models.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Models;

public sealed class AgentTextAuditTests
{
    [Fact]
    public void HasInvisibleCharacters_DetectsZeroWidthAndControlUnits()
    {
        Assert.True(AgentTextAudit.HasInvisibleCharacters("1\u200B"));
        Assert.True(AgentTextAudit.HasInvisibleCharacters("1\u000B"));
        Assert.True(AgentTextAudit.HasInvisibleCharacters("1\u202E"));
        Assert.False(AgentTextAudit.HasInvisibleCharacters("A-101"));
    }

    [Fact]
    public void Visual_StripsZeroWidthAndCollapsesUnicodeSpaces()
    {
        Assert.Equal("1", AgentTextAudit.Visual("1\u200B"));
        Assert.Equal("A 1", AgentTextAudit.Visual("A\u00A0\u00A01"));
        Assert.Equal("A 1", AgentTextAudit.Visual("A\u200B 1"));
        Assert.Equal("Общие данные", AgentTextAudit.Visual("Общие данные"));
    }

    [Fact]
    public void CodePoints_ReturnsExactUnits()
    {
        Assert.Equal(new[] { 8203, 49 }, AgentTextAudit.CodePoints("\u200B1"));
        Assert.Equal(new[] { 49, 0x1F600 }, AgentTextAudit.CodePoints("1\uD83D\uDE00"));
    }

    [Fact]
    public void CodePoints_KeepsLoneSurrogateAsCodeUnit()
    {
        var points = AgentTextAudit.CodePoints("a\uD800");

        Assert.Equal(2, points.Count);
        Assert.Equal(0xD800, points[1]);
    }
}