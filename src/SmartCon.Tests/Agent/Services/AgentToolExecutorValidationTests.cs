using System.Text.Json;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

public sealed class AgentToolExecutorValidationTests
{
    private const string StrictSchema =
        """{"type":"object","properties":{"viewId":{"type":"integer"}},"additionalProperties":false}""";

    [Fact]
    public async Task InvalidArguments_ReturnsInvalidArgumentsWithErrorsAndHint()
    {
        var tool = new ScriptedTool(
            "write_tool", AgentToolRisk.Read, supportsDryRun: false, schema: StrictSchema);
        var executor = CreateExecutor(tool);

        var result = await executor.ExecuteAsync(
            "write_tool", Args("""{"viewId":"abc"}"""), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("invalid_arguments", result.ErrorCode);
        Assert.Contains("fix the listed paths", result.Message, StringComparison.Ordinal);
        Assert.NotNull(result.Data);
        var errors = result.Data!.Value.GetProperty("errors");
        Assert.Contains(
            errors.EnumerateArray(),
            error => error.GetString()!.Contains("expected an integer"));
        Assert.Empty(tool.Executions);
    }

    [Fact]
    public async Task RepairedArguments_ArePassedToTheTool_AndResultCarriesRepairs()
    {
        var tool = new ScriptedTool(
            "write_tool", AgentToolRisk.Read, supportsDryRun: false, schema: StrictSchema);
        var executor = CreateExecutor(tool);

        var result = await executor.ExecuteAsync(
            "write_tool", Args("""{"view_id":"7"}"""), CancellationToken.None);

        Assert.True(result.Success);
        var execution = Assert.Single(tool.Executions);
        Assert.Contains("\"viewId\":7", execution.ArgumentsJson, StringComparison.Ordinal);
        Assert.Contains(result.Repairs, repair => repair.Contains("renamed 'view_id' -> 'viewId'"));

        var serialized = ToolResultBudget.Serialize(result);
        Assert.Contains("\"_repairs\"", serialized, StringComparison.Ordinal);
        Assert.Contains("renamed 'view_id'", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyRepairs_AreOmittedFromTheSerializedPayload()
    {
        var serialized = ToolResultBudget.Serialize(ToolResult.Ok());

        Assert.DoesNotContain("_repairs", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownTool_SuggestsTheClosestNames()
    {
        var executor = CreateExecutor(
            new ScriptedTool("list_views", AgentToolRisk.Read, supportsDryRun: false),
            new ScriptedTool("rename_views", AgentToolRisk.Read, supportsDryRun: false));

        var result = await executor.ExecuteAsync(
            "list_view", Args("{}"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("unknown_tool", result.ErrorCode);
        Assert.Contains("Did you mean", result.Message, StringComparison.Ordinal);
        Assert.NotNull(result.Data);
        var suggestions = result.Data!.Value.GetProperty("suggestions");
        Assert.Contains(
            suggestions.EnumerateArray(),
            suggestion => suggestion.GetString() == "list_views");
    }

    [Fact]
    public async Task UnknownTool_UnrelatedName_HasNoSuggestions()
    {
        var executor = CreateExecutor(
            new ScriptedTool("list_views", AgentToolRisk.Read, supportsDryRun: false));

        var result = await executor.ExecuteAsync(
            "zzzzzzzz", Args("{}"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("unknown_tool", result.ErrorCode);
        Assert.NotNull(result.Data);
        Assert.Empty(result.Data!.Value.GetProperty("suggestions").EnumerateArray());
    }

    [Fact]
    public async Task PartiallyRepairedArguments_ReportRepairsAndRemainingErrors()
    {
        var tool = new ScriptedTool(
            "write_tool", AgentToolRisk.Read, supportsDryRun: false, schema: StrictSchema);
        var executor = CreateExecutor(tool);

        var result = await executor.ExecuteAsync(
            "write_tool",
            Args("""{"view_id":"abc","bogus":1}"""),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("invalid_arguments", result.ErrorCode);
        Assert.NotNull(result.Data);
        var repairs = result.Data!.Value.GetProperty("repairs");
        Assert.Contains(
            repairs.EnumerateArray(),
            repair => repair.GetString()!.Contains("renamed 'view_id' -> 'viewId'"));
        Assert.Contains(
            repairs.EnumerateArray(),
            repair => repair.GetString()!.Contains("dropped unknown property 'bogus'"));
        Assert.Contains(
            result.Data.Value.GetProperty("errors").EnumerateArray(),
            error => error.GetString()!.Contains("expected an integer"));
        Assert.Empty(tool.Executions);
    }

    private static AgentToolExecutor CreateExecutor(params IAgentTool[] tools) => new(
        new AgentToolRegistry(tools),
        new ImmediateAwaitableEvent(),
        new AgentSessionContext { DocumentId = "doc" },
        new RecordingConfirmation());

    private static JsonElement Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
