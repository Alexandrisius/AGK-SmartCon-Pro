using SmartCon.Agent.Services.Decisions;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

public sealed class RuleToolRouterTests
{
    private static AgentToolDefinition Tool(
        string name,
        string description,
        params string[] tags) =>
        new()
        {
            Name = name,
            Description = description,
            Risk = name.StartsWith("list", StringComparison.Ordinal)
                   || name.StartsWith("get", StringComparison.Ordinal)
                ? AgentToolRisk.Read
                : AgentToolRisk.Write,
            InputSchemaJson = @"{""type"":""object"",""properties"":{}}",
            Tags = tags
        };

    private static readonly IReadOnlyList<AgentToolDefinition> Catalog = new[]
    {
        Tool("list_views", "List project views", "виды", "список"),
        Tool("list_levels", "List levels", "уровни"),
        Tool("query_elements", "Search elements", "найти", "поиск"),
        Tool("summarize_elements", "Count elements", "сколько", "количество"),
        Tool("get_element_details", "Element parameters", "параметры", "детали"),
        Tool("get_project_overview", "Project overview", "проект", "обзор"),
        Tool("find_tools", "Find a tool", "инструмент"),
        Tool("rename_views", "Rename views", "переименовать", "имя"),
        Tool("rename_levels", "Rename levels", "переименовать", "уровень"),
        Tool("set_view_crop", "Crop region", "подрезка", "crop", "область"),
        Tool("set_view_visibility", "Hide categories", "скрыть", "видимость"),
        Tool("copy_view_settings", "Copy settings from a reference view",
            "скопировать", "как эталон"),
        Tool("undo_last", "Undo the last change", "отменить", "откат"),
        Tool("execute_plan", "Run a multi-step plan", "план", "шаги")
    };

    [Fact]
    public async Task RussianRenameRequest_SelectsRenameTool()
    {
        var decision = await new RuleToolRouter().RouteAsync(
            "Переименуй вид SC_Eval_A в новое имя", Catalog);

        Assert.False(decision.Fallback);
        Assert.Contains(decision.Tools, tool => tool.Name == "rename_views");
        Assert.True(decision.Tools.Count <= 14);
    }

    [Fact]
    public async Task CropRequest_SelectsCropTool()
    {
        var decision = await new RuleToolRouter().RouteAsync(
            "Включи область подрезки у вида", Catalog);

        Assert.Contains(decision.Tools, tool => tool.Name == "set_view_crop");
    }

    [Fact]
    public async Task ImperativeVerb_MatchesInfinitiveTag()
    {
        var tools = new List<AgentToolDefinition>
        {
            Tool("copy_view_settings", "Copy settings", "скопировать", "эталон"),
            Tool("rename_views", "Rename views", "переименовать")
        };

        var copy = await new RuleToolRouter().RouteAsync("скопируй настройки", tools);
        var rename = await new RuleToolRouter().RouteAsync("переименуй вид", tools);

        Assert.Contains(copy.Tools, tool => tool.Name == "copy_view_settings");
        Assert.Contains(rename.Tools, tool => tool.Name == "rename_views");
    }

    [Fact]
    public async Task NoSignal_FallsBackToFullCatalog()
    {
        var decision = await new RuleToolRouter().RouteAsync("xyzzy", Catalog);

        Assert.True(decision.Fallback);
        Assert.Equal(Catalog.Count, decision.Tools.Count);
    }

    [Fact]
    public async Task CoreReadTools_AreAlwaysIncluded()
    {
        var decision = await new RuleToolRouter().RouteAsync(
            "Переименуй вид", Catalog);

        foreach (var core in RuleToolRouter.CoreToolNames)
        {
            if (Catalog.Any(tool => tool.Name == core))
                Assert.Contains(decision.Tools, tool => tool.Name == core);
        }
    }

    [Fact]
    public async Task Shortlist_IsCapped()
    {
        var many = Enumerable.Range(0, 40)
            .Select(index => Tool("write_tool_" + index, "rename views", "переименовать"))
            .ToList();

        var decision = await new RuleToolRouter().RouteAsync("переименуй вид", many, maxTools: 12);

        Assert.True(decision.Tools.Count <= 12);
    }
}

public sealed class DecisionsLayerTests
{
    private static AgentToolDefinition Tool(string name, string description) =>
        new()
        {
            Name = name,
            Description = description,
            Risk = AgentToolRisk.Write,
            InputSchemaJson = @"{""type"":""object"",""properties"":{}}"
        };

    [Fact]
    public async Task DecisionRouter_ChoiceIsAddedToCoreReadSet()
    {
        var client = new StubDecisionClient();
        client.Answers["catalog_match"] = new AgentDecisionAnswer { Type = "noul", Noul = 0.9 };
        client.Answers["tool"] = new AgentDecisionAnswer
        {
            Type = "choice",
            Choice = "rename_views",
            Confidence = 0.8,
            Probabilities = new Dictionary<string, double>
            {
                ["rename_views"] = 0.8,
                ["list_views"] = 0.15
            }
        };
        var router = new DecisionsToolRouter(new RuleToolRouter(), client);
        var tools = new List<AgentToolDefinition>
        {
            Tool("list_views", "List views"),
            Tool("rename_views", "Rename views")
        };

        var decision = await router.RouteAsync("переименуй вид", tools);

        Assert.Contains(decision.Tools, tool => tool.Name == "rename_views");
        Assert.Contains(decision.Tools, tool => tool.Name == "list_views");
    }

    [Fact]
    public async Task DecisionRouter_DisabledClient_FallsBackToRuleRouter()
    {
        var client = new StubDecisionClient { Enabled = false };
        var router = new DecisionsToolRouter(new RuleToolRouter(), client);
        var tools = new List<AgentToolDefinition> { Tool("rename_views", "Rename views") };

        var decision = await router.RouteAsync("переименуй вид", tools);

        Assert.Contains(decision.Tools, tool => tool.Name == "rename_views");
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task DecisionRouter_Error_FallsBackToRuleRouter()
    {
        var client = new StubDecisionClient { Throw = true };
        var router = new DecisionsToolRouter(new RuleToolRouter(), client);
        var tools = new List<AgentToolDefinition> { Tool("rename_views", "Rename view names") };

        var decision = await router.RouteAsync("переименуй вид", tools);

        Assert.Contains(decision.Tools, tool => tool.Name == "rename_views");
    }

    [Fact]
    public void HttpDecisionClient_ParsesNoulAndChoice()
    {
        const string json = """
        {"model":"pplx-decider-v1.3","answers":{
          "intent":{"type":"choice","choice":"rename_views",
            "probabilities":{"rename_views":0.83,"apply_view_template":0.15},"confidence":0.75},
          "is_ru":{"type":"noul","noul":0.998},
          "refused":{"type":"refusal"}}}
        """;

        var answers = HttpDecisionClient.Parse(json);

        Assert.Equal("rename_views", answers["intent"].Choice);
        Assert.Equal(0.83, answers["intent"].Probabilities!["rename_views"], 3);
        Assert.Equal(0.998, answers["is_ru"].Noul!.Value, 3);
        Assert.Equal("refusal", answers["refused"].Error);
        Assert.False(answers["refused"].IsAnswered);
    }

    [Fact]
    public void HttpDecisionClient_ParsesUsageCostIntoTracker()
    {
        const string json = """
        {"answers":{"tool":{"type":"choice","choice":"list_views","confidence":0.9}},
         "usage":{"input_tokens":128,"output_tokens":0,"cost":0.0000384}}
        """;

        var tracker = new DecisionCostTracker();
        var answers = HttpDecisionClient.Parse(json, out var costUsd, tracker);

        Assert.Equal("list_views", answers["tool"].Choice);
        Assert.Equal(0.0000384m, costUsd);
        Assert.Equal(0.0000384m, tracker.TotalUsd);
    }
}

public sealed class AgentToolSchemaValidatorTests
{
    private const string Schema = """
    {"type":"object","properties":{
      "items":{"type":"array","minItems":1,"maxItems":3,
        "items":{"type":"object","properties":{
          "viewId":{"type":"integer"},
          "newName":{"type":"string"},
          "flag":{"type":"boolean"}},
        "required":["viewId"],"additionalProperties":false}},
      "mode":{"type":"string","enum":["a","b"]},
      "dryRun":{"type":"boolean","default":true}},
    "required":["items"],"additionalProperties":false}
    """;

    [Fact]
    public void ValidArguments_Pass()
    {
        var errors = AgentToolSchemaValidator.Validate(
            Schema,
            """{"items":[{"viewId":1,"newName":"x","flag":true}],"mode":"a","dryRun":false}""");

        Assert.Empty(errors);
    }

    [Fact]
    public void RequiredPropertyMissing_IsReported()
    {
        var errors = AgentToolSchemaValidator.Validate(Schema, """{"mode":"a"}""");

        Assert.Contains(errors, error => error.Contains("items"));
    }

    [Fact]
    public void UnknownProperty_IsReported()
    {
        var errors = AgentToolSchemaValidator.Validate(
            Schema,
            """{"items":[{"viewId":1,"bogus":1}]}""");

        Assert.Contains(errors, error => error.Contains("bogus"));
    }

    [Fact]
    public void WrongType_IsReported()
    {
        var errors = AgentToolSchemaValidator.Validate(
            Schema,
            """{"items":[{"viewId":"not-a-number"}]}""");

        Assert.Contains(errors, error => error.Contains("expected an integer"));
    }

    [Fact]
    public void EnumViolation_IsReported()
    {
        var errors = AgentToolSchemaValidator.Validate(
            Schema,
            """{"items":[{"viewId":1}],"mode":"c"}""");

        Assert.Contains(errors, error => error.Contains("not one of the allowed options"));
    }

    [Fact]
    public void ArrayLimits_AreReported()
    {
        var errors = AgentToolSchemaValidator.Validate(
            Schema,
            """{"items":[{"viewId":1},{"viewId":2},{"viewId":3},{"viewId":4}]}""");

        Assert.Contains(errors, error => error.Contains("at most 3"));
    }

    [Fact]
    public void InvalidJson_IsReported()
    {
        var errors = AgentToolSchemaValidator.Validate(Schema, "{");

        Assert.Contains(errors, error => error.Contains("not valid JSON"));
    }
}
