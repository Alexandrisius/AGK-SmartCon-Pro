using System.Text.Json;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// find_tools must be able to say "no" (C10-03, C7-07), must always return the
/// tool the query names (C1-04) and must respect its limit.
/// </summary>
public sealed class FindToolsNoMatchTests
{
    private static AgentToolContext Context() =>
        new(new ImmediateAwaitableEvent(), "doc", Guid.Empty);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>The production router with its decision model switched off.</summary>
    private static FindToolsTool RuleTool(params IAgentTool[] tools)
    {
        var tool = new FindToolsTool(new RuleToolRouter());
        tool.Attach(new FakeToolRegistry(tools));
        return tool;
    }

    private static async Task<JsonElement> SearchAsync(FindToolsTool tool, string argumentsJson)
    {
        var result = await tool.ExecuteAsync(Context(), Parse(argumentsJson), CancellationToken.None);
        Assert.True(result.Success, result.Message);
        return result.Data!.Value;
    }

    private static IAgentTool[] Catalog() => new IAgentTool[]
    {
        new ScriptedTool("rename_views"),
        new ScriptedTool("set_view_range"),
        new ScriptedTool("copy_view_settings"),
        new ScriptedTool("apply_view_template"),
        new ScriptedTool("get_view_template_details", AgentToolRisk.Read, supportsDryRun: false)
    };

    [Fact]
    public async Task Search_NothingSharesWording_SaysNoToolMatches()
    {
        var data = await SearchAsync(RuleTool(Catalog()), "{\"query\":\"унитаз запорная арматура\"}");

        Assert.True(data.GetProperty("noMatch").GetBoolean());
        Assert.Equal(0, data.GetProperty("total").GetInt32());
        Assert.Equal(0, data.GetProperty("tools").GetArrayLength());
        Assert.Equal(0, data.GetProperty("closest").GetArrayLength());
        // Nothing came close: every populated pack is named so the model can browse.
        Assert.Contains("views", data.GetProperty("packs").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public async Task Search_OneCommonWordOnly_IsNoMatchAndNamesTheClosestEntries()
    {
        var data = await SearchAsync(RuleTool(Catalog()), "{\"query\":\"zzzqqq view\"}");

        Assert.True(data.GetProperty("noMatch").GetBoolean());
        Assert.Equal(0, data.GetProperty("tools").GetArrayLength());
        var closest = data.GetProperty("closest").EnumerateArray()
            .Select(entry => entry.GetProperty("name").GetString())
            .ToList();
        Assert.NotEmpty(closest);
        Assert.Contains("rename_views", closest);
        // Closest entries are returned with their pack, without schemas.
        var first = data.GetProperty("closest")[0];
        Assert.Equal("name", first.EnumerateObject().First().Name);
        Assert.Contains("views", data.GetProperty("packs").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public async Task Search_NoMatch_MessageIsExplicit()
    {
        var tool = RuleTool(Catalog());

        var result = await tool.ExecuteAsync(Context(), Parse("{\"query\":\"унитаз\"}"), CancellationToken.None);

        Assert.StartsWith("No tool matches in the catalog", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_RelevantQuery_StillReturnsCards()
    {
        var data = await SearchAsync(RuleTool(Catalog()), "{\"query\":\"rename views\"}");

        Assert.False(data.TryGetProperty("noMatch", out _));
        Assert.Contains(
            data.GetProperty("tools").EnumerateArray(),
            card => card.GetProperty("name").GetString() == "rename_views");
    }

    [Fact]
    public async Task Search_QueryNamesATool_ItLeadsTheAnswerInsideTheLimit()
    {
        // get_view_template_details ranks low for its own name: "view" and "template"
        // are shared with half of the catalog. With limit=1 it must still be the answer.
        var data = await SearchAsync(RuleTool(Catalog()), "{\"query\":\"get_view_template_details\",\"limit\":1}");

        var tools = data.GetProperty("tools").EnumerateArray().ToList();
        Assert.Single(tools);
        Assert.Equal("get_view_template_details", tools[0].GetProperty("name").GetString());
        Assert.False(data.TryGetProperty("noMatch", out _));
    }

    [Fact]
    public async Task Search_ToolNameInsideASentence_IsFoundAsAWholeName()
    {
        var tool = RuleTool(
            new ScriptedTool("set_view_range"),
            new ScriptedTool("set_view_range_batch"));

        var exact = await SearchAsync(tool, "{\"query\":\"use set_view_range for view 3\",\"limit\":1}");
        Assert.Equal("set_view_range", exact.GetProperty("tools")[0].GetProperty("name").GetString());

        // A longer name is not "the same name": the shorter tool must not be pulled in.
        var longer = await SearchAsync(tool, "{\"query\":\"set_view_range_batch\",\"limit\":1}");
        Assert.Equal("set_view_range_batch", longer.GetProperty("tools")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Search_SeveralNamedTools_NeverExceedTheLimit()
    {
        var data = await SearchAsync(
            RuleTool(Catalog()),
            "{\"query\":\"rename_views set_view_range copy_view_settings\",\"limit\":2}");

        var names = data.GetProperty("tools").EnumerateArray()
            .Select(card => card.GetProperty("name").GetString())
            .ToList();
        Assert.Equal(2, names.Count);
        Assert.Equal(2, data.GetProperty("total").GetInt32());
        Assert.Contains("rename_views", names);
    }

    [Fact]
    public async Task Search_NamedTool_IsReturnedEvenWithoutRankableContext()
    {
        var only = new ScriptedTool("get_view_template_details", AgentToolRisk.Read, supportsDryRun: false);
        var data = await SearchAsync(RuleTool(only), "{\"query\":\"get_view_template_details\"}");

        Assert.Equal("get_view_template_details", data.GetProperty("tools")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void QueryCoverage_UnknownTermOutweighsACommonWord()
    {
        var tools = new[]
        {
            Definition("rename_views", "rename views"),
            Definition("list_views", "list views")
        };

        var covered = ToolCardRouter.QueryCoverage("rename views", tools);
        Assert.Equal(1.0, covered["rename_views"], 6);
        var generic = ToolCardRouter.QueryCoverage("rename geometry", tools);

        Assert.Equal(0.0, generic["list_views"], 6);
        Assert.True(generic["rename_views"] < ToolCardRouter.MinQueryCoverage);
    }

    private static AgentToolDefinition Definition(string name, string text) => new()
    {
        Name = name,
        Description = text,
        Risk = AgentToolRisk.Write,
        InputSchemaJson = "{}",
        Purpose = text
    };
}
