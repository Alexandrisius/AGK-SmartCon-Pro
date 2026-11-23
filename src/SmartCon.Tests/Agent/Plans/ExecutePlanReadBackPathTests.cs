using System.Text.Json;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Plans;

/// <summary>
/// Read-back path resolution of AgentPlanExecutor on the payload shapes the
/// read tools actually return (night2 findings C4-10/C9-04/C1-03): names with
/// dots, byName over flat value maps, bracket literal keys, and unsupported
/// paths reported as notVerifiable instead of false mismatches.
/// </summary>
public sealed class ExecutePlanReadBackPathTests
{
    private static AgentPlanStep Step(params AgentPlanExpectation[] expectations) => new()
    {
        StepId = "s1",
        ToolName = "write_tool",
        ArgumentsJson = @"{""items"":[{""viewId"":1}]}",
        ExpectedChanges = 1,
        ReadBackTool = "read_tool",
        ArgumentsReadBackJson = "{}",
        ReadBackExpectations = expectations
    };

    private static AgentPlan Plan(AgentPlanStep step) =>
        new() { Goal = "plan", Steps = new[] { step } };

    private static AgentPlanExecutionContext Context() =>
        new() { DocumentId = "doc", ApprovalMode = AgentApprovalMode.ConfirmWrites };

    private static (AgentPlanExecutor Executor, ScriptedTool WriteTool) Create(
        Func<ToolResult> readHandler)
    {
        var writeTool = new ScriptedTool("write_tool");
        var readTool = new ScriptedTool(
            "read_tool",
            AgentToolRisk.Read,
            supportsDryRun: false,
            handler: _ => readHandler(),
            schema: @"{""type"":""object"",""properties"":{},""additionalProperties"":false}");
        var registry = new AgentToolRegistry(new IAgentTool[] { writeTool, readTool });
        return (new AgentPlanExecutor(
            registry,
            new ImmediateAwaitableEvent(),
            new RecordingConfirmation(),
            new RecordingAuditLog()), writeTool);
    }

    private static JsonElement Payload(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task ViewNameWithDot_StaysOneSegment()
    {
        // list_views payload: views[].name may contain dots ("View 1.2"); the
        // dot must not shatter the path (C4-10).
        var (executor, _) = Create(() => ToolResult.Ok(
            Payload("""
            {"views":[
              {"viewId":1,"name":"Reference","templateName":null},
              {"viewId":2,"name":"View 1.2","templateName":"Clean"}
            ]}
            """),
            "read"));

        var result = await executor.ExecuteAsync(
            Plan(Step(new AgentPlanExpectation
            {
                Path = "views.byName[View 1.2].templateName",
                Expected = "Clean"
            })),
            Context(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Mismatches);
        Assert.Empty(result.NotVerifiable);
        Assert.Contains(result.Verified, line => line.Contains("View 1.2"));
    }

    [Fact]
    public async Task FamilyTypeValues_AreAddressedByNameAndBracketKey()
    {
        // list_family_types payload: types[].values is a flat {parameter: value}
        // map — values.byName[name] and the bracket literal form must both work
        // (C9-04).
        var (executor, _) = Create(() => ToolResult.Ok(
            Payload("""
            {"familyId":9,"familyName":"Fam","types":[
              {"typeId":11,"name":"Type A","familyId":9,
               "values":{"Width":"1200 mm","Mark":"W.1"}}
            ]}
            """),
            "read"));

        var result = await executor.ExecuteAsync(
            Plan(Step(
                new AgentPlanExpectation
                {
                    Path = "types.byId[11].values.byName[Width]",
                    Expected = "1200 mm"
                },
                new AgentPlanExpectation
                {
                    Path = "types.byId[11].values.[Mark]",
                    Expected = "W.1"
                })),
            Context(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Mismatches);
        Assert.Empty(result.NotVerifiable);
        Assert.Equal(2, result.ExpectationsChecked);
    }

    [Fact]
    public async Task ViewsByIdAndViewName_ResolveListViewsCards()
    {
        // The two documented address forms for list_views rows (C1-03).
        var (executor, _) = Create(() => ToolResult.Ok(
            Payload("""
            {"total":2,"views":[
              {"viewId":1,"name":"View A","templateName":"T1"},
              {"viewId":2,"name":"View B","templateName":null}
            ]}
            """),
            "read"));

        var result = await executor.ExecuteAsync(
            Plan(Step(
                new AgentPlanExpectation
                {
                    Path = "views.byId[1].templateName",
                    Expected = "T1"
                },
                new AgentPlanExpectation
                {
                    Path = "views.byName[View A].templateName",
                    Expected = "T1"
                })),
            Context(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Mismatches);
        Assert.Equal(2, result.ExpectationsChecked);
    }

    [Fact]
    public async Task UnsupportedPath_IsNotVerifiable_NotMismatch()
    {
        // A path that does not address the payload must not fabricate a
        // mismatch with actual 'null' (C4-10/C9-04).
        var (executor, _) = Create(() => ToolResult.Ok(
            Payload("""
            {"familyId":9,"types":[{"typeId":11,"name":"Type A"}]}
            """),
            "read"));

        var result = await executor.ExecuteAsync(
            Plan(Step(
                new AgentPlanExpectation
                {
                    Path = "types.byId[11].values.byName[Missing param]",
                    Expected = "1200 mm"
                },
                new AgentPlanExpectation
                {
                    Path = "wrongShape.segment",
                    Expected = "x"
                })),
            Context(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Mismatches);
        Assert.Equal(2, result.NotVerifiable.Count);
        Assert.All(result.NotVerifiable,
            line => Assert.Contains("path form not supported", line, StringComparison.Ordinal));
        Assert.Equal(2, result.ExpectationsChecked);
        Assert.Equal(2, result.Steps[0].NotVerifiableCount);
    }

    [Fact]
    public async Task ExplicitNullValue_IsStillAMismatch()
    {
        // A path that resolves to an explicit JSON null is real information:
        // it stays a mismatch, not a notVerifiable escape hatch.
        var (executor, _) = Create(() => ToolResult.Ok(
            Payload("""
            {"views":[{"viewId":2,"name":"View A","templateName":null}]}
            """),
            "read"));

        var result = await executor.ExecuteAsync(
            Plan(Step(new AgentPlanExpectation
            {
                Path = "views.byName[View A].templateName",
                Expected = "T1"
            })),
            Context(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(result.Mismatches);
        Assert.Empty(result.NotVerifiable);
        Assert.Contains(result.Mismatches,
            line => line.Contains("actual 'null'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CreatedViewsBatch_AreAddressableByName()
    {
        // create_views_batch creates views the apply just added: the read-back
        // list_views answer contains them, and byName addresses the created
        // row without knowing its id (C4-11).
        var (executor, _) = Create(() => ToolResult.Ok(
            Payload("""
            {"total":1,"views":[{"viewId":444,"name":"Plan A","templateName":"T1"}]}
            """),
            "read"));

        var result = await executor.ExecuteAsync(
            Plan(Step(new AgentPlanExpectation
            {
                Path = "views.byName[Plan A].templateName",
                Expected = "T1"
            })),
            Context(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Mismatches);
        Assert.Equal(1, result.ExpectationsChecked);
    }
}
