using System.Text.Json;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Plans;

/// <summary>Plan read-back resolution and the family id alias re-targeting of later steps.</summary>
public sealed partial class AgentPlanExecutorTests
{
    [Fact]
    public async Task ReadBack_ByElementId_ResolvesTheTargetCard()
    {
        var writeTool = new ScriptedTool("write_tool");
        var readTool = new ScriptedTool(
            "read_tool",
            AgentToolRisk.Read,
            supportsDryRun: false,
            handler: _ => ToolResult.Ok(
                JsonSerializer.SerializeToElement(new
                {
                    views = new[]
                    {
                        new { viewId = 1, name = "reference", templateName = (string?)null },
                        new { viewId = 2, name = "target", templateName = (string?)"T" }
                    }
                }),
                "read"),
            schema: @"{""type"":""object"",""properties"":{""viewId"":{""type"":""integer""}},""additionalProperties"":false}");
        var confirmation = new RecordingConfirmation();
        var executor = CreateExecutor(
            writeTool, readTool, confirmation, new RecordingAuditLog());
        var step = new AgentPlanStep
        {
            StepId = "s1",
            ToolName = "write_tool",
            ArgumentsJson = WriteArguments,
            ExpectedChanges = 1,
            ReadBackTool = "read_tool",
            ArgumentsReadBackJson = "{}",
            ReadBackExpectations = new[]
            {
                new AgentPlanExpectation { Path = "byId[2].templateName", Expected = "T" }
            }
        };

        var result = await executor.ExecuteAsync(
            Plan(step), Context(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Mismatches);
        Assert.Equal(1, result.ExpectationsChecked);
        Assert.Contains(result.Verified, line => line.Contains("byId[2].templateName"));
    }

    [Fact]
    public async Task ReadBack_ByName_ResolvesFlatFamilyParameterArray()
    {
        var writeTool = new ScriptedTool("write_tool");
        var readTool = new ScriptedTool(
            "read_tool",
            AgentToolRisk.Read,
            supportsDryRun: false,
            handler: _ => ToolResult.Ok(
                JsonSerializer.SerializeToElement(new
                {
                    parameters = new object[]
                    {
                        new
                        {
                            id = "-1010101",
                            name = "Ширина",
                            scope = "type",
                            valuesByType = new object[]
                            {
                                new { typeName = "Тип 1", displayValue = "1200 мм" }
                            }
                        },
                        new
                        {
                            id = "-1010102",
                            name = "Высота",
                            scope = "instance",
                            valuesByType = Array.Empty<object>()
                        }
                    }
                }),
                "read"),
            schema: @"{""type"":""object"",""properties"":{},""additionalProperties"":false}");
        var executor = CreateExecutor(
            writeTool, readTool, new RecordingConfirmation(), new RecordingAuditLog());
        var step = new AgentPlanStep
        {
            StepId = "s1",
            ToolName = "write_tool",
            ArgumentsJson = WriteArguments,
            ExpectedChanges = 1,
            ReadBackTool = "read_tool",
            ArgumentsReadBackJson = "{}",
            ReadBackExpectations = new[]
            {
                new AgentPlanExpectation
                {
                    Path = "parameters.byName[Ширина].valuesByType.byName[Тип 1].displayValue",
                    Expected = "1200 мм"
                },
                new AgentPlanExpectation
                {
                    Path = "parameters.byId[-1010101].name",
                    Expected = "Ширина"
                }
            }
        };

        var result = await executor.ExecuteAsync(
            Plan(step), Context(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Mismatches);
        Assert.Equal(2, result.ExpectationsChecked);
    }

    [Fact]
    public async Task ReadBack_BracketIndexPath_ResolvesTheSameAsDotIndex()
    {
        var writeTool = new ScriptedTool("write_tool");
        var readTool = new ScriptedTool(
            "read_tool",
            AgentToolRisk.Read,
            supportsDryRun: false,
            handler: _ => ToolResult.Ok(
                JsonSerializer.SerializeToElement(new
                {
                    categories = new[]
                    {
                        new { categoryId = 1, name = "Walls" },
                        new { categoryId = 2, name = "Pipes" }
                    }
                }),
                "read"),
            schema: @"{""type"":""object"",""properties"":{},""additionalProperties"":false}");
        var executor = CreateExecutor(
            writeTool, readTool, new RecordingConfirmation(), new RecordingAuditLog());
        var step = new AgentPlanStep
        {
            StepId = "s1",
            ToolName = "write_tool",
            ArgumentsJson = WriteArguments,
            ExpectedChanges = 1,
            ReadBackTool = "read_tool",
            ArgumentsReadBackJson = "{}",
            ReadBackExpectations = new[]
            {
                // The bracket form is what tool output shows; "filter.categories[0].name"
                // used to report mismatch even though the apply succeeded (B4).
                new AgentPlanExpectation { Path = "categories[0].name", Expected = "Walls" },
                new AgentPlanExpectation { Path = "categories.1.name", Expected = "Pipes" }
            }
        };

        var result = await executor.ExecuteAsync(
            Plan(step), Context(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(result.Mismatches);
        Assert.Equal(2, result.ExpectationsChecked);
    }

    [Fact]
    public async Task FamilyIdAlias_ReTargetsLaterApplySteps()
    {
        const string schema =
            @"{""type"":""object"",""properties"":{""familyId"":{""type"":""integer""}," +
            @"""dryRun"":{""type"":""boolean""}},""additionalProperties"":false}";
        var writeTool = new ScriptedTool(
            "family_write_tool",
            handler: args =>
            {
                var dryRun = args.TryGetProperty("dryRun", out var flag)
                             && flag.ValueKind == JsonValueKind.True;
                var payload = dryRun
                    ? JsonSerializer.SerializeToElement(new
                    {
                        dryRun = true,
                        changed = new[] { new { id = 1 } },
                        skipped = Array.Empty<object>(),
                        failed = Array.Empty<object>(),
                        total = 1,
                        affected = 1
                    })
                    : JsonSerializer.SerializeToElement(new
                    {
                        dryRun = false,
                        familyId = 2,
                        previousFamilyId = 1,
                        changed = new[] { new { id = 1 } },
                        skipped = Array.Empty<object>(),
                        failed = Array.Empty<object>(),
                        total = 1,
                        affected = 1
                    });
                return ToolResult.Ok(payload, "ok", 1);
            },
            schema: schema);
        var executor = CreateExecutor(
            writeTool,
            ReadTool(),
            new RecordingConfirmation(),
            new RecordingAuditLog());

        var steps = new[]
        {
            new AgentPlanStep
            {
                StepId = "s1",
                ToolName = "family_write_tool",
                ArgumentsJson = @"{""familyId"":1}",
                ExpectedChanges = 1
            },
            new AgentPlanStep
            {
                StepId = "s2",
                ToolName = "family_write_tool",
                ArgumentsJson = @"{""familyId"":1}",
                ExpectedChanges = 1,
                DependsOn = new[] { "s1" }
            }
        };

        var result = await executor.ExecuteAsync(
            Plan(steps), Context(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.PreviewIncomplete);
        Assert.Contains("s2", result.NotPreviewed);
        Assert.Equal(3, writeTool.Executions.Count);
        Assert.Contains("\"familyId\":2", writeTool.Executions[^1].ArgumentsJson);
    }
}
