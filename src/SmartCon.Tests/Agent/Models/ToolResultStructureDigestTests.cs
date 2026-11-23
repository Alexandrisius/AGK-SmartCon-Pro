using System.Text.Json;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Models;

/// <summary>
/// A trimmed result that lost its payload must still say what the payload held
/// (DB-09), and the tool-name echo must survive every trimming stage (DB-01).
/// </summary>
public sealed class ToolResultStructureDigestTests
{
    private static ToolResult LargeResult(string? tool = null)
    {
        var payload = new
        {
            total = 120,
            items = Enumerable.Range(0, 120)
                .Select(index => new { id = index, name = "item " + index, values = new[] { 1, 2 } })
                .ToArray(),
            summary = new { rows = 120, columns = new[] { "id", "name" } },
            dryRun = true,
            padding = new string('x', 20000)
        };
        var result = ToolResult.Ok(JsonSerializer.SerializeToElement(payload), "huge");
        return tool is null ? result : result.WithToolName(tool);
    }

    private static JsonElement Digest(ToolResult result)
    {
        var json = ToolResultBudget.Serialize(result, maxCharacters: 4000, resultHandle: "call_1");
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public void Digest_DescribesEveryTopLevelKey()
    {
        var structure = Digest(LargeResult()).GetProperty("structure");

        Assert.Equal("array", structure.GetProperty("items").GetProperty("kind").GetString());
        Assert.Equal(120, structure.GetProperty("items").GetProperty("length").GetInt32());
        Assert.Equal("object", structure.GetProperty("summary").GetProperty("kind").GetString());
        Assert.Equal(2, structure.GetProperty("summary").GetProperty("count").GetInt32());
        Assert.Equal("scalar", structure.GetProperty("total").GetProperty("kind").GetString());
        Assert.Equal(120, structure.GetProperty("total").GetProperty("value").GetInt32());
        Assert.Equal("scalar", structure.GetProperty("dryRun").GetProperty("kind").GetString());
        Assert.Equal("scalar", structure.GetProperty("padding").GetProperty("kind").GetString());
        // A long scalar is described, not copied.
        Assert.False(structure.GetProperty("padding").TryGetProperty("value", out _));
    }

    [Fact]
    public void Digest_NamesTheReadyArrayPaths()
    {
        var paths = Digest(LargeResult()).GetProperty("arrayPaths").EnumerateArray()
            .ToDictionary(
                entry => entry.GetProperty("path").GetString()!,
                entry => entry.GetProperty("length").GetInt32());

        Assert.Equal(120, paths["data.items"]);
        Assert.Equal(2, paths["data.summary.columns"]);
        Assert.Equal(2, paths["data.items.0.values"]);
    }

    [Fact]
    public void Digest_StaysWithinTheBudget()
    {
        var json = ToolResultBudget.Serialize(LargeResult(), maxCharacters: 4000, resultHandle: "call_1");

        Assert.True(json.Length <= 4000, "length=" + json.Length);
    }

    [Fact]
    public void Digest_WithoutArraysHasNoPathList()
    {
        var digest = Digest(ToolResult.Ok(
            JsonSerializer.SerializeToElement(new { note = "x", padding = new string('x', 20000) }), "huge"));

        Assert.True(digest.GetProperty("structure").TryGetProperty("note", out _));
        Assert.False(digest.TryGetProperty("arrayPaths", out _));
    }

    [Fact]
    public void Digest_ArrayPath_ResolvesWithTheStoredResultReader()
    {
        // The path the digest names must be the path read_tool_result accepts.
        var full = ToolResultBudget.SerializeFull(LargeResult());
        var digest = Digest(LargeResult());
        var path = digest.GetProperty("arrayPaths")[0].GetProperty("path").GetString()!;

        var resolved = AgentResultPath.Resolve(full, path, out var error);

        Assert.Null(error);
        Assert.NotNull(resolved);
        Assert.Equal(120, ((System.Text.Json.Nodes.JsonArray)resolved!).Count);
    }

    [Fact]
    public void Serialize_ToolEcho_IsKeptByTheDigestAndTheMinimalEnvelope()
    {
        var trimmed = ToolResultBudget.Serialize(LargeResult("rename_views"), 4000, "call_1");
        using var document = JsonDocument.Parse(trimmed);
        Assert.Equal("rename_views", document.RootElement.GetProperty("tool").GetString());

        // An oversized message pushes the envelope down to the minimal shape.
        var loud = LargeResult("rename_views").WithMessageSuffix(new string('m', 9000));
        var minimal = ToolResultBudget.Serialize(loud, 4000, "call_1");
        using var minimalDocument = JsonDocument.Parse(minimal);
        Assert.Equal("rename_views", minimalDocument.RootElement.GetProperty("tool").GetString());
    }

    [Fact]
    public void WithToolName_SurvivesWarningsSkillsAndSuffixes()
    {
        var result = ToolResult.Ok(null, "ok").WithToolName("set_parameters");

        var derived = result
            .WithWarnings(new[] { "hint" })
            .WithSkills(new[] { new ToolResultSkill { Pack = "views", Markdown = "# m" } })
            .WithDocumentVersion("v1")
            .WithMessageSuffix(" [truncated]")
            .WithRepairs(new[] { "r" });

        Assert.Equal("set_parameters", derived.Tool);
        // An echo already present wins over a second one; an empty name adds nothing.
        Assert.Equal(
            "set_parameters",
            ToolResult.Ok(null, "ok").WithToolName("set_parameters").WithToolName("x").Tool);
        Assert.Null(ToolResult.Ok(null, "ok").WithToolName(null).Tool);
        Assert.Null(ToolResult.Ok().Tool);
    }
}
