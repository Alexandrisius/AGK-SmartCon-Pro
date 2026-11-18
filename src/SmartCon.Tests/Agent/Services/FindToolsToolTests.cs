using System.Text.Json;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

public sealed class FindToolsToolTests
{
    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static AgentToolContext Context() =>
        new(new ImmediateAwaitableEvent(), "doc", Guid.Empty);

    [Fact]
    public async Task FindTools_UsesConfiguredRouterAndPassesContext()
    {
        var write = new ScriptedTool("write_tool");
        var read = new ScriptedTool("read_tool", AgentToolRisk.Read, supportsDryRun: false);
        var registry = new FakeToolRegistry(write, read);
        var router = new RecordingToolRouter
        {
            Result = new ToolRoutingDecision { Tools = new[] { write.Definition } }
        };
        var tool = new FindToolsTool(router);
        tool.Attach(registry);

        var result = await tool.ExecuteAsync(
            Context(),
            Parse("{\"query\":\"переименуй\",\"context\":\"нашел 3 вида SC_Eval\"}"),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(router.LastQuery);
        Assert.Contains("переименуй", router.LastQuery);
        Assert.Contains("нашел 3 вида SC_Eval", router.LastQuery);
        Assert.Equal(8, router.LastMaxTools);
        var tools = result.Data!.Value.GetProperty("tools");
        Assert.True(tools.GetArrayLength() >= 1);
        Assert.Contains(tools.EnumerateArray(), tool =>
            string.Equals(
                tool.GetProperty("name").GetString(), "write_tool", StringComparison.Ordinal));
        Assert.Contains(tools.EnumerateArray(), tool =>
            tool.GetProperty("risk").GetString() == "Write");
    }

    [Fact]
    public async Task FindTools_CapsContextLength()
    {
        var write = new ScriptedTool("write_tool");
        var router = new RecordingToolRouter
        {
            Result = new ToolRoutingDecision { Tools = new[] { write.Definition } }
        };
        var tool = new FindToolsTool(router);
        tool.Attach(new FakeToolRegistry(write));
        var longContext = new string('x', 5000);

        await tool.ExecuteAsync(
            Context(),
            Parse("{\"query\":\"q\",\"context\":\"" + longContext + "\"}"),
            CancellationToken.None);

        Assert.NotNull(router.LastQuery);
        Assert.True(router.LastQuery!.Length < 2000);
    }

    [Fact]
    public async Task FindTools_WithoutQuery_IsRejected()
    {
        var write = new ScriptedTool("write_tool");
        var tool = new FindToolsTool(new RecordingToolRouter());
        tool.Attach(new FakeToolRegistry(write));

        var result = await tool.ExecuteAsync(
            Context(), Parse("{\"context\":\"only context\"}"), CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("invalid_arguments", result.ErrorCode);
    }
}
