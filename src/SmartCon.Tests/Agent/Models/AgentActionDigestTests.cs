using Xunit;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Tests.Agent.Models;

public sealed class AgentActionDigestTests
{
    [Fact]
    public void Compute_IsKeyOrderIndependent()
    {
        var first = AgentActionDigest.Compute(
            "rename_views",
            """{"dryRun":false,"items":[{"viewId":1,"newName":"A"}]}""",
            "doc");
        var second = AgentActionDigest.Compute(
            "rename_views",
            """{ "items": [ { "newName": "A", "viewId": 1 } ], "dryRun": false }""",
            "doc");

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public void Compute_ChangesWithArguments()
    {
        var first = AgentActionDigest.Compute("rename_views", """{"items":[]}""", "doc");
        var second = AgentActionDigest.Compute("rename_views", """{"items":[{"viewId":1}]}""", "doc");
        var third = AgentActionDigest.Compute("rename_views", """{"items":[]}""", "other-doc");

        Assert.NotEqual(first, second);
        Assert.NotEqual(first, third);
    }

    [Fact]
    public void Compute_DifferentTools_Differ()
    {
        var first = AgentActionDigest.Compute("rename_views", "{}", "doc");
        var second = AgentActionDigest.Compute("rename_levels", "{}", "doc");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void CanonicalizeJson_SortsNestedKeys()
    {
        var canonical = AgentActionDigest.CanonicalizeJson(
            """{"b":2,"a":{"z":[{"y":1,"x":2}],"c":true}}""");

        Assert.Equal("""{"a":{"c":true,"z":[{"x":2,"y":1}]},"b":2}""", canonical);
    }

    [Fact]
    public void Compute_EmptyArguments_IsStable()
    {
        var first = AgentActionDigest.Compute("undo_last", string.Empty, "doc");
        var second = AgentActionDigest.Compute("undo_last", "{}", "doc");

        Assert.Equal(first, second);
    }
}
