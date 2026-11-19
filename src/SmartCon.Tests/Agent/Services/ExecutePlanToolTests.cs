using System.Text.Json;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

public sealed class ExecutePlanToolTests
{
    private const string PlanArgs = """
    {"goal":"rename two views","steps":[
      {"stepId":"s1","toolName":"write_tool","arguments":{"items":[{"viewId":1}]},
       "expectedChanges":1}
    ]}
    """;

    private static ExecutePlanTool CreateTool(ScriptedTool writeTool) =>
        new(() => new FakeToolRegistry(writeTool), new ImmediateAwaitableEvent(),
            new RecordingAuditLog());

    private static AgentToolContext Context(bool approved) =>
        new(new ImmediateAwaitableEvent(), "doc", Guid.Empty) { WriteApproved = approved };

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string ApplyArgs(string digest) =>
        PlanArgs.Replace(
            "\"steps\"",
            "\"dryRun\":false,\"planDigest\":\"" + digest + "\",\"steps\"");

    [Fact]
    public async Task Preview_RunsDryRunOnly_AndReportsDiff()
    {
        var writeTool = new ScriptedTool("write_tool");
        var tool = CreateTool(writeTool);

        var result = await tool.ExecuteAsync(
            Context(approved: false), Parse(PlanArgs), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Single(writeTool.Executions);
        Assert.Contains("\"dryRun\":true", writeTool.Executions[0].ArgumentsJson);
        Assert.True(result.Data!.Value.GetProperty("dryRun").GetBoolean());
        var previewItems = result.Data.Value.GetProperty("steps")[0].GetProperty("items");
        Assert.True(previewItems.GetProperty("dryRun").GetBoolean());
        Assert.Equal(1, result.Data.Value.GetProperty("affected").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(
            result.Data.Value.GetProperty("planDigest").GetString()));
    }

    [Fact]
    public async Task Apply_WithoutApproval_IsRejected()
    {
        var writeTool = new ScriptedTool("write_tool");
        var tool = CreateTool(writeTool);

        var result = await tool.ExecuteAsync(
            Context(approved: false),
            Parse(PlanArgs.Replace("\"steps\"", "\"dryRun\":false,\"steps\"")),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("confirmation_required", result.ErrorCode);
        Assert.Empty(writeTool.Executions);
    }

    [Fact]
    public async Task Apply_AfterApproval_RunsDryRunThenApply()
    {
        var writeTool = new ScriptedTool("write_tool");
        var tool = CreateTool(writeTool);

        var preview = await tool.ExecuteAsync(
            Context(approved: false), Parse(PlanArgs), CancellationToken.None);
        var digest = preview.Data!.Value.GetProperty("planDigest").GetString()!;

        var result = await tool.ExecuteAsync(
            Context(approved: true), Parse(ApplyArgs(digest)), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(3, writeTool.Executions.Count);
        Assert.Contains("\"dryRun\":false", writeTool.Executions[2].ArgumentsJson);
        Assert.True(writeTool.Executions[2].Approved);
        Assert.False(result.Data!.Value.GetProperty("dryRun").GetBoolean());
        var appliedItems = result.Data.Value.GetProperty("steps")[0].GetProperty("items");
        Assert.False(appliedItems.GetProperty("dryRun").GetBoolean());
        Assert.Equal(
            0,
            result.Data.Value.GetProperty("planReadBack")
                .GetProperty("expectationsChecked")
                .GetInt32());
        Assert.Contains(
            "No plan read-back expectations",
            result.Data.Value.GetProperty("hint").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_WithoutPreviewDigest_IsRejected()
    {
        var writeTool = new ScriptedTool("write_tool");
        var tool = CreateTool(writeTool);

        var result = await tool.ExecuteAsync(
            Context(approved: true),
            Parse(PlanArgs.Replace("\"steps\"", "\"dryRun\":false,\"steps\"")),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("plan_digest_required", result.ErrorCode);
        Assert.Empty(writeTool.Executions);
    }

    [Fact]
    public async Task Apply_WithDigestOfAnotherPlan_IsRejected()
    {
        var writeTool = new ScriptedTool("write_tool");
        var tool = CreateTool(writeTool);
        // A different plan means different executable intent: the goal text is no
        // longer part of the digest, so change a step argument instead.
        var otherPlan = PlanArgs.Replace("\"viewId\":1", "\"viewId\":2");

        var preview = await tool.ExecuteAsync(
            Context(approved: false), Parse(otherPlan), CancellationToken.None);
        var otherDigest = preview.Data!.Value.GetProperty("planDigest").GetString()!;
        var executionsAfterPreview = writeTool.Executions.Count;

        var result = await tool.ExecuteAsync(
            Context(approved: true), Parse(ApplyArgs(otherDigest)), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("plan_digest_mismatch", result.ErrorCode);
        Assert.Equal(executionsAfterPreview, writeTool.Executions.Count);
    }

    [Fact]
    public async Task UnknownStepTool_FailsValidationBeforeTouchingTools()
    {
        var writeTool = new ScriptedTool("write_tool");
        var tool = CreateTool(writeTool);

        var result = await tool.ExecuteAsync(
            Context(approved: false),
            Parse("""
            {"goal":"bad plan","steps":[
              {"stepId":"s1","toolName":"missing_tool","arguments":{}}
            ]}
            """),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("plan_invalid", result.ErrorCode);
        Assert.Empty(writeTool.Executions);
    }

    [Theory]
    [InlineData("execute_plan")]
    [InlineData("Execute_Plan")]
    public async Task NestedExecutePlan_IsRejected(string nestedName)
    {
        var writeTool = new ScriptedTool("write_tool");
        var tool = CreateTool(writeTool);

        var result = await tool.ExecuteAsync(
            Context(approved: false),
            Parse("""
            {"goal":"recursive","steps":[
              {"stepId":"s1","toolName":"NESTED","arguments":{"goal":"x","steps":[]}}
            ]}
            """.Replace("NESTED", nestedName)),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("plan_invalid", result.ErrorCode);
        Assert.Empty(writeTool.Executions);
    }

    [Fact]
    public async Task MissingSteps_IsRejected()
    {
        var tool = CreateTool(new ScriptedTool("write_tool"));

        var result = await tool.ExecuteAsync(
            Context(approved: false),
            Parse("""{"goal":"empty"}"""),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("plan_invalid", result.ErrorCode);
    }
}
