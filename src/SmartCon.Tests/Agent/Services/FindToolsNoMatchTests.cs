using System.Text.Json;
using SmartCon.Agent.Services.Decisions;
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
        // One word out of two is not half the request: the unknown one outweighs it.
        Assert.True(generic["rename_views"] < 0.5);
    }

    [Fact]
    public void QueryCoverage_FloorLetsALongRequestThrough()
    {
        // The night run: "schedule fields sort create schedule" reached 0.47 on
        // create_schedule and was refused, although the tool exists. A floor of
        // half the query turned 40% of live searches into false "no match":
        // in a real catalog most words of such a request are shared by many cards.
        var tools = new[]
        {
            Definition("create_schedule", "create schedule fields"),
            Definition("create_views_batch", "create views"),
            Definition("create_view_filter", "create filter fields"),
            Definition("create_sheet", "create sheet fields"),
            Definition("get_schedule_definition", "schedule fields definition"),
            Definition("get_schedule_data", "schedule data"),
            Definition("set_schedule_sort", "schedule sort"),
            Definition("set_schedule_filter", "schedule filter fields")
        };

        var covered = ToolCardRouter.QueryCoverage("schedule fields sort create schedule", tools);
        var best = covered.Values.Max();

        // The request passes the floor, and the tool it asks for by name is
        // covered well below half of it — a floor of 0.5 refused this search.
        Assert.True(best >= ToolCardRouter.MinQueryCoverage);
        Assert.InRange(covered["create_schedule"], ToolCardRouter.MinQueryCoverage, 0.5);
    }

    private sealed class CardTool : IAgentTool
    {
        public CardTool(AgentToolDefinition definition)
        {
            Definition = definition;
        }

        public AgentToolDefinition Definition { get; }

        public Task<ToolResult> ExecuteAsync(
            AgentToolContext context,
            JsonElement args,
            CancellationToken ct) =>
            Task.FromResult(ToolResult.Ok(message: "ok"));
    }

    /// <summary>
    /// Seven short cards and one long card that alone carries the whole request:
    /// BM25 over descriptions pays for the length of the long one.
    /// </summary>
    private static IAgentTool[] LengthPenalizedCatalog()
    {
        var tools = new List<IAgentTool>();
        for (var index = 1; index <= 7; index++)
            tools.Add(new CardTool(Definition("short_card_" + index, "семейства модель")));

        var filler = string.Join(" ", Enumerable.Repeat("загруженные типы категория экземпляр", 30));
        tools.Add(new CardTool(new AgentToolDefinition
        {
            Name = "list_families",
            Description = filler + " семейства модель",
            Risk = AgentToolRisk.Read,
            InputSchemaJson = "{}",
            Purpose = "какие семейства есть в модели список"
        }));
        return tools.ToArray();
    }

    [Fact]
    public async Task Search_WordingShortlistMissesTheCardThatCarriesTheWholeQuery_TheCardLeads()
    {
        // Night run 2: list_families came to explain placedInstanceCount in its
        // description, the description ranking paid for the length, and "какие
        // семейства есть в модели" no longer listed the tool.
        var data = await SearchAsync(
            RuleTool(LengthPenalizedCatalog()), "{\"query\":\"семейства модель список\"}");

        Assert.False(data.TryGetProperty("noMatch", out _));
        Assert.Equal("list_families", data.GetProperty("tools")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Search_DecisionModelPickedAnotherTool_TheCoveredCardDoesNotDisplaceThePick()
    {
        // A router with a verdict of its own is not second-guessed by wording.
        var client = new StubDecisionClient();
        client.Answers["tool"] = new AgentDecisionAnswer
        {
            Type = "choice",
            Choice = "short_card_1",
            Confidence = 0.8
        };

        var data = await SearchAsync(
            DecisionTool(client, LengthPenalizedCatalog()),
            "{\"query\":\"семейства модель список\",\"limit\":2}");

        var names = data.GetProperty("tools").EnumerateArray()
            .Select(card => card.GetProperty("name").GetString())
            .ToList();
        Assert.Equal(new[] { "short_card_1", "list_families" }, names);
    }

    /// <summary>The production router with its decision model switched on.</summary>
    private static FindToolsTool DecisionTool(StubDecisionClient client, params IAgentTool[] tools)
    {
        var tool = new FindToolsTool(new DecisionsToolRouter(new RuleToolRouter(), client));
        tool.Attach(new FakeToolRegistry(tools));
        return tool;
    }

    private static AgentDecisionAnswer NoCatalogMatch() =>
        new() { Type = "noul", Noul = 0.1 };

    [Fact]
    public async Task Search_DecisionModelSaysNothingFits_AnswersNoMatch()
    {
        // The decision layer was on in the night run, and with it on, "no match"
        // could not come at all: every miss was answered with cards.
        var client = new StubDecisionClient();
        client.Answers["catalog_match"] = NoCatalogMatch();
        var data = await SearchAsync(
            DecisionTool(client, Catalog()),
            "{\"query\":\"координаты положение элемента location point bounding box\"}");

        Assert.True(data.GetProperty("noMatch").GetBoolean());
        Assert.Equal(0, data.GetProperty("tools").GetArrayLength());
    }

    [Fact]
    public async Task Search_DecisionModelSaysNothingFits_ButTheQueryNamesATool_TheToolIsAnswered()
    {
        var client = new StubDecisionClient();
        client.Answers["catalog_match"] = NoCatalogMatch();
        var data = await SearchAsync(
            DecisionTool(client, Catalog()), "{\"query\":\"use set_view_range for view 3\"}");

        Assert.False(data.TryGetProperty("noMatch", out _));
        Assert.Contains(
            data.GetProperty("tools").EnumerateArray(),
            card => card.GetProperty("name").GetString() == "set_view_range");
    }

    [Fact]
    public async Task Search_DecisionModelGivesNoAnswer_TheWordingFloorStillGuards()
    {
        // No answer is not a no: the rule shortlist stands, behind the same floor.
        var client = new StubDecisionClient();

        var miss = await SearchAsync(DecisionTool(client, Catalog()), "{\"query\":\"zzzqqq view\"}");
        Assert.True(miss.GetProperty("noMatch").GetBoolean());

        var hit = await SearchAsync(DecisionTool(client, Catalog()), "{\"query\":\"rename views\"}");
        Assert.False(hit.TryGetProperty("noMatch", out _));
        Assert.Contains(
            hit.GetProperty("tools").EnumerateArray(),
            card => card.GetProperty("name").GetString() == "rename_views");
    }

    [Fact]
    public async Task Search_DecisionModelPicks_ItsPickLeadsAndNoCatalogDumpFollows()
    {
        // Wording ranking finds no signal for this query, so the rule router
        // returns the whole catalog. Behind the pick that dump is no choice.
        var client = new StubDecisionClient();
        client.Answers["catalog_match"] = new AgentDecisionAnswer { Type = "noul", Noul = 0.9 };
        client.Answers["tool"] = new AgentDecisionAnswer
        {
            Type = "choice",
            Choice = "copy_view_settings",
            Confidence = 0.7
        };

        var data = await SearchAsync(
            DecisionTool(client, Catalog()), "{\"query\":\"сделай вид как эталонный\"}");

        var names = data.GetProperty("tools").EnumerateArray()
            .Select(card => card.GetProperty("name").GetString())
            .ToList();
        Assert.False(data.TryGetProperty("noMatch", out _));
        Assert.Equal(new[] { "copy_view_settings" }, names);
    }

    [Fact]
    public async Task DecisionRouter_ReportsWhatItPickedAndWhatItRefused()
    {
        var pick = new StubDecisionClient();
        pick.Answers["catalog_match"] = new AgentDecisionAnswer { Type = "noul", Noul = 0.9 };
        pick.Answers["tool"] = new AgentDecisionAnswer
        {
            Type = "choice",
            Choice = "rename_views",
            Confidence = 0.8
        };
        var silent = new StubDecisionClient();
        var refusal = new StubDecisionClient();
        refusal.Answers["catalog_match"] = NoCatalogMatch();
        var tools = new[]
        {
            new ScriptedTool("rename_views").Definition,
            new ScriptedTool("rename_levels").Definition
        };

        var picked = await new DecisionsToolRouter(new RuleToolRouter(), pick)
            .RouteAsync("переименуй виды", tools, 6);
        var none = await new DecisionsToolRouter(new RuleToolRouter(), silent)
            .RouteAsync("переименуй виды", tools, 6);
        var refused = await new DecisionsToolRouter(new RuleToolRouter(), refusal)
            .RouteAsync("переименуй виды", tools, 6);

        Assert.Equal(new[] { "rename_views" }, picked.Picked);
        Assert.False(picked.NoCatalogMatch);
        Assert.Empty(none.Picked);
        Assert.False(none.NoCatalogMatch);
        Assert.Empty(refused.Picked);
        Assert.True(refused.NoCatalogMatch);

        // A pick the catalog does not hold is no pick: the shortlist is the rule's.
        pick.Answers["tool"] = new AgentDecisionAnswer
        {
            Type = "choice",
            Choice = "tool_that_does_not_exist",
            Confidence = 0.8
        };
        var invented = await new DecisionsToolRouter(new RuleToolRouter(), pick)
            .RouteAsync("переименуй виды", tools, 6);
        Assert.Empty(invented.Picked);
        Assert.Contains(invented.Tools, tool => tool.Name == "rename_views");
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
