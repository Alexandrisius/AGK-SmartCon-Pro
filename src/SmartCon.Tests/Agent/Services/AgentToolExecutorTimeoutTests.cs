using System.Diagnostics;
using System.Text.Json;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

public sealed class AgentToolExecutorTimeoutTests
{
    [Fact]
    public async Task SlowTool_IsCancelledAndReportedAsToolTimeout()
    {
        var tool = new TimedTool(
            "slow_tool",
            timeoutMs: 50,
            async ct =>
            {
                await Task.Delay(5000, ct);
                return ToolResult.Ok();
            });
        var executor = CreateExecutor(tool);
        var stopwatch = Stopwatch.StartNew();

        var result = await executor.ExecuteAsync("slow_tool", Args("{}"), CancellationToken.None);

        stopwatch.Stop();
        Assert.False(result.Success);
        Assert.Equal("tool_timeout", result.ErrorCode);
        Assert.Contains("50 ms", result.Message, StringComparison.Ordinal);
        Assert.Contains("TimeoutMs", result.Message, StringComparison.Ordinal);
        Assert.True(stopwatch.ElapsedMilliseconds < 4000);
        Assert.NotNull(result.Data);
        Assert.False(result.Data!.Value.GetProperty("stillRunning").GetBoolean());
    }

    [Fact]
    public async Task FastTool_IsNotAffectedByTheTimeout()
    {
        var tool = new TimedTool(
            "fast_tool",
            timeoutMs: 30000,
            async ct =>
            {
                await Task.Delay(10, ct);
                return ToolResult.Ok(message: "quick");
            });
        var executor = CreateExecutor(tool);

        var result = await executor.ExecuteAsync("fast_tool", Args("{}"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("quick", result.Message);
    }

    [Fact]
    public async Task ToolException_IsPropagatedUnchanged()
    {
        var tool = new TimedTool(
            "throwing_tool",
            timeoutMs: 30000,
            _ => Task.FromException<ToolResult>(new InvalidOperationException("boom")));
        var executor = CreateExecutor(tool);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync("throwing_tool", Args("{}"), CancellationToken.None));

        Assert.Equal("boom", exception.Message);
    }

    [Fact]
    public async Task ToolIgnoringCancellation_IsReportedAsStillRunning()
    {
        var tool = new TimedTool(
            "stuck_tool",
            timeoutMs: 50,
            async _ =>
            {
                await Task.Delay(6000, CancellationToken.None);
                return ToolResult.Ok();
            });
        var executor = CreateExecutor(tool);
        var stopwatch = Stopwatch.StartNew();

        var result = await executor.ExecuteAsync("stuck_tool", Args("{}"), CancellationToken.None);

        stopwatch.Stop();
        Assert.False(result.Success);
        Assert.Equal("tool_timeout", result.ErrorCode);
        Assert.True(stopwatch.ElapsedMilliseconds >= 4500);
        Assert.NotNull(result.Data);
        Assert.True(result.Data!.Value.GetProperty("stillRunning").GetBoolean());
    }

    [Fact]
    public async Task ZeroTimeout_FallsBackToThirtySeconds()
    {
        var tool = new TimedTool(
            "default_timeout_tool",
            timeoutMs: 0,
            _ => Task.FromResult(ToolResult.Ok(message: "done")));
        var executor = CreateExecutor(tool);

        var result = await executor.ExecuteAsync(
            "default_timeout_tool", Args("{}"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("done", result.Message);
    }

    [Fact]
    public async Task SlowConfirmation_DoesNotConsumeTheToolTimeout()
    {
        var tool = new TimedTool(
            "confirmed_tool",
            timeoutMs: 50,
            _ => Task.FromResult(ToolResult.Ok(message: "done")),
            AgentToolRisk.Write);
        var confirmation = new RecordingConfirmation
        {
            OnRequest = _ => Task.Delay(200)
        };
        var executor = new AgentToolExecutor(
            new AgentToolRegistry(new[] { tool }),
            new ImmediateAwaitableEvent(),
            new AgentSessionContext { DocumentId = "doc" },
            confirmation);

        var result = await executor.ExecuteAsync(
            "confirmed_tool", Args("{}"), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("done", result.Message);
        Assert.Single(confirmation.Requests);
    }

    private static AgentToolExecutor CreateExecutor(IAgentTool tool) => new(
        new AgentToolRegistry(new[] { tool }),
        new ImmediateAwaitableEvent(),
        new AgentSessionContext { DocumentId = "doc" },
        new RecordingConfirmation());

    private static JsonElement Args(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class TimedTool : IAgentTool
    {
        private readonly Func<CancellationToken, Task<ToolResult>> _body;

        public TimedTool(
            string name,
            int timeoutMs,
            Func<CancellationToken, Task<ToolResult>> body,
            AgentToolRisk risk = AgentToolRisk.Read)
        {
            _body = body;
            Definition = new AgentToolDefinition
            {
                Name = name,
                Description = name + " test tool",
                Risk = risk,
                InputSchemaJson = "{}",
                TimeoutMs = timeoutMs
            };
        }

        public AgentToolDefinition Definition { get; }

        public Task<ToolResult> ExecuteAsync(
            AgentToolContext context,
            JsonElement args,
            CancellationToken ct) => _body(ct);
    }
}
