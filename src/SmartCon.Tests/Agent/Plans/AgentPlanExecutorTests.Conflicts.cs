using System.Text.Json;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Plans;

/// <summary>Cross-step template conflicts of the plan preflight (review F1 x WA).</summary>
public sealed partial class AgentPlanExecutorTests
{
    [Fact]
    public async Task PlanStepConflict_TemplateControlsCrop()
    {
        var templateTool = new ScriptedTool(
            "apply_view_template",
            handler: _ => ToolResult.Ok(
                JsonSerializer.SerializeToElement(new
                {
                    changed = new[] { new { id = 1 } },
                    skipped = Array.Empty<object>(),
                    failed = Array.Empty<object>(),
                    total = 1,
                    affected = 1,
                    delta = new[]
                    {
                        new
                        {
                            viewId = 1,
                            templateId = 9,
                            templateName = "T",
                            controls = new { crop = true, visibility = true, viewRange = true }
                        }
                    }
                }),
                "template"));
        var cropTool = new ScriptedTool("set_view_crop");
        var registry = new AgentToolRegistry(
            new IAgentTool[] { templateTool, cropTool, ReadTool() });
        var executor = new AgentPlanExecutor(
            registry,
            new ImmediateAwaitableEvent(),
            new RecordingConfirmation(),
            new RecordingAuditLog());

        var steps = new[]
        {
            new AgentPlanStep
            {
                StepId = "tpl",
                ToolName = "apply_view_template",
                ArgumentsJson = @"{""items"":[{""viewId"":1,""templateId"":9}]}",
                ExpectedChanges = 1
            },
            new AgentPlanStep
            {
                StepId = "crop",
                ToolName = "set_view_crop",
                ArgumentsJson = @"{""items"":[{""viewId"":1,""crop"":{}}]}",
                ExpectedChanges = 1,
                DependsOn = new[] { "tpl" }
            }
        };

        var result = await executor.ExecuteAsync(
            Plan(steps), Context(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("plan_step_conflict", result.ErrorCode);
        Assert.Contains("crop", result.Message, StringComparison.Ordinal);
        Assert.Contains("tpl", result.Message, StringComparison.Ordinal);
    }

    private static ScriptedTool TemplateTool(string deltaItem) =>
        new(
            "apply_view_template",
            handler: _ => ToolResult.Ok(
                JsonSerializer.SerializeToElement(new
                {
                    changed = new[] { new { id = 1 } },
                    skipped = Array.Empty<object>(),
                    failed = Array.Empty<object>(),
                    total = 1,
                    affected = 1,
                    delta = new[] { JsonSerializer.Deserialize<object>(deltaItem) }
                }),
                "template"));

    private static ScriptedTool FilterTool() =>
        new(
            "create_view_filter",
            handler: _ => ToolResult.Ok(
                JsonSerializer.SerializeToElement(new
                {
                    changed = new[] { new { id = 1 } },
                    skipped = Array.Empty<object>(),
                    failed = Array.Empty<object>(),
                    total = 1,
                    affected = 1
                }),
                "filter"),
            schema:
            @"{""type"":""object"",""properties"":{""viewIds"":{""type"":""array"",""items"":{""type"":""integer""}}," +
            @"""visibility"":{""type"":""string""},""dryRun"":{""type"":""boolean""}},""additionalProperties"":false}");

    private static ScriptedTool InclusionTool() =>
        new(
            "set_view_template_inclusion",
            handler: _ => ToolResult.Ok(
                JsonSerializer.SerializeToElement(new
                {
                    changed = new[] { new { id = 1 } },
                    skipped = Array.Empty<object>(),
                    failed = Array.Empty<object>(),
                    total = 1,
                    affected = 1,
                    templateId = 9,
                    parameters = new[]
                    {
                        new { section = "filters", included = false }
                    }
                }),
                "inclusion"),
            schema:
            @"{""type"":""object"",""properties"":{""templateId"":{""type"":""integer""}," +
            @"""parameters"":{""type"":""array"",""items"":{""type"":""string""}}," +
            @"""included"":{""type"":""boolean""},""dryRun"":{""type"":""boolean""}}," +
            @"""additionalProperties"":false}");

    private static AgentPlanExecutor FilterPlanExecutor(params IAgentTool[] extraTools)
    {
        var tools = new List<IAgentTool> { FilterTool() };
        tools.AddRange(extraTools);
        tools.Add(ReadTool());
        return new AgentPlanExecutor(
            new AgentToolRegistry(tools),
            new ImmediateAwaitableEvent(),
            new RecordingConfirmation(),
            new RecordingAuditLog());
    }

    private static AgentPlanStep TemplateStep(string id = "tpl") => new()
    {
        StepId = id,
        ToolName = "apply_view_template",
        ArgumentsJson = @"{""items"":[{""viewId"":1,""templateId"":9}]}",
        ExpectedChanges = 1
    };

    private static AgentPlanStep FilterStep(string id = "flt", string? dependsOn = "tpl") => new()
    {
        StepId = id,
        ToolName = "create_view_filter",
        ArgumentsJson =
            @"{""viewIds"":[1],""visibility"":""hide"",""dryRun"":false}",
        ExpectedChanges = 1,
        DependsOn = dependsOn is null ? Array.Empty<string>() : new[] { dependsOn }
    };

    [Fact]
    public async Task PlanStepConflict_TemplateControlsOnlyVisibility_AllowsFilterStep()
    {
        // F1 × WA: a template that controls model/annotation graphics but NOT
        // "V/G Overrides Filters" does not stop create_view_filter — the tool's
        // `visibility` argument is the filter's own state, not a
        // VIS_GRAPHICS_MODEL write. The detector used to reject this plan.
        var executor = FilterPlanExecutor(
            TemplateTool(
                """
                {"viewId":1,"templateId":9,"templateName":"T","templateControlsFilters":false,
                 "controls":{"crop":false,"visibility":true,"viewRange":false}}
                """));

        var result = await executor.ExecuteAsync(
            Plan(TemplateStep(), FilterStep()), Context(), CancellationToken.None);

        Assert.True(result.Success, result.Message);
    }

    [Fact]
    public async Task PlanStepConflict_TemplateControlsFilters_RejectsFilterStep_WithEscape()
    {
        // The other side: a template that includes "V/G Overrides Filters"
        // really does take over the filter list — create_view_filter on the
        // view would fail with view_controlled_by_template. The conflict
        // message must name the two workable paths.
        var executor = FilterPlanExecutor(
            TemplateTool(
                """
                {"viewId":1,"templateId":9,"templateName":"T","templateControlsFilters":true,
                 "controls":{"crop":false,"visibility":false,"viewRange":false}}
                """));

        var result = await executor.ExecuteAsync(
            Plan(TemplateStep(), FilterStep()), Context(), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("plan_step_conflict", result.ErrorCode);
        Assert.Contains("filters", result.Message, StringComparison.Ordinal);
        Assert.Contains(
            "set_view_template_inclusion(templateId=9, parameters=[\"filters\"], included=false)",
            result.Message, StringComparison.Ordinal);
        Assert.Contains("viewIds=[9]", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanStepConflict_DetachedFilters_TemplateThenFilterRuns()
    {
        // The advised escape, executed: detach 'filters' from the template
        // first, then apply it, then create the per-view filter — the plan
        // must pass end to end.
        var executor = FilterPlanExecutor(
            TemplateTool(
                """
                {"viewId":1,"templateId":9,"templateName":"T","templateControlsFilters":false,
                 "controls":{"crop":false,"visibility":true,"viewRange":false}}
                """),
            InclusionTool());

        var result = await executor.ExecuteAsync(
            Plan(
                new AgentPlanStep
                {
                    StepId = "detach",
                    ToolName = "set_view_template_inclusion",
                    ArgumentsJson =
                        @"{""templateId"":9,""parameters"":[""filters""],""included"":false,""dryRun"":false}",
                    ExpectedChanges = 1
                },
                TemplateStep(),
                FilterStep(dependsOn: null)),
            Context(),
            CancellationToken.None);

        Assert.True(result.Success, result.Message);
    }
}
