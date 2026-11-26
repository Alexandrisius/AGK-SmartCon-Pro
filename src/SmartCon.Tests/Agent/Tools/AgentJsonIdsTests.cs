using System.Text.Json;
using SmartCon.Revit.Agent.Tools;
using Xunit;

namespace SmartCon.Tests.Agent.Tools;

public sealed class AgentJsonIdsTests
{
    [Fact]
    public void Scan_FindsElementIdsAtAnyDepth()
    {
        var args = Parse(
            "{\"items\":[{\"elementId\":101,\"parameter\":\"Mark\"}," +
            "{\"elementId\":102}],\"viewIds\":[201,202],\"levelId\":301}");

        var found = AgentJsonIds.Scan(args).ToList();

        Assert.Equal(new long[] { 101, 102, 201, 202, 301 }, found.Select(item => item.Id));
    }

    [Fact]
    public void Scan_SkipsIdsThatAreNotElementIds()
    {
        var args = Parse(
            "{\"categoryId\":-2000011,\"categoryIds\":[-2000011,-2000022]," +
            "\"worksetId\":7,\"parameterId\":-1001203,\"elementId\":55}");

        var found = AgentJsonIds.Scan(args).ToList();

        Assert.Single(found);
        Assert.Equal(55, found[0].Id);
    }

    [Fact]
    public void Scan_IgnoresStringsAndNonPositiveNumbers()
    {
        var args = Parse("{\"elementId\":\"55\",\"viewId\":0,\"sheetId\":-1,\"name\":\"x\"}");

        Assert.Empty(AgentJsonIds.Scan(args));
    }

    [Fact]
    public void ScanJson_InvalidJson_ReturnsEmpty()
    {
        Assert.Empty(AgentJsonIds.ScanJson("{not json"));
        Assert.Empty(AgentJsonIds.ScanJson(null));
        Assert.Empty(AgentJsonIds.ScanJson("[]"));
    }

    [Fact]
    public void ScanUniqueIds_FindsOwnerAndElementUniqueIds()
    {
        var found = AgentJsonIds.ScanUniqueIds(
            "{\"ownerUniqueId\":\"abc\",\"items\":[{\"elementUniqueId\":\"def\"}]," +
            "\"uniqueName\":\"not-an-id\"}").ToList();

        Assert.Equal(new[] { "abc", "def" }, found);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
