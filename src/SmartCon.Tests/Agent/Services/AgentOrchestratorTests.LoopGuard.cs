using SmartCon.Agent.Services.Llm;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

public sealed partial class AgentOrchestratorTests
{
    [Fact]
    public async Task RunTurn_ThirdIdenticalCall_IsBlockedWithoutExecution()
    {
        var store = new InMemoryConversationStore();
        var tool = new CountingTool();
        var registry = new AgentToolRegistry(new IAgentTool[] { tool });
        var executor = CreateExecutor(registry);
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant(
            null,
            new[]
            {
                new AgentToolCall { Id = "call_1", Name = "get_test", ArgumentsJson = "{}" },
                new AgentToolCall { Id = "call_2", Name = "get_test", ArgumentsJson = "{}" },
                new AgentToolCall { Id = "call_3", Name = "get_test", ArgumentsJson = "{}" }
            });
        llm.EnqueueAssistant("done");
        var orchestrator = CreateOrchestrator(llm, executor, registry, store);

        var result = await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "цикл",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        Assert.Equal("done", result.FinalText);
        Assert.Equal(3, result.ToolCallCount);
        Assert.Equal(2, tool.Calls);

        var toolMessages = llm.Requests[1].Messages
            .Where(message => message.Role == AgentRole.Tool)
            .ToList();
        Assert.Equal(3, toolMessages.Count);
        Assert.Contains("loop_detected", toolMessages[2].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTurn_FiveIdenticalCalls_AbortsTheTurn()
    {
        var store = new InMemoryConversationStore();
        var tool = new CountingTool();
        var registry = new AgentToolRegistry(new IAgentTool[] { tool });
        var executor = CreateExecutor(registry);
        var usageStore = new CapturingUsageStore();
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant(
            null,
            new[]
            {
                new AgentToolCall { Id = "call_1", Name = "get_test", ArgumentsJson = "{}" },
                new AgentToolCall { Id = "call_2", Name = "get_test", ArgumentsJson = "{}" },
                new AgentToolCall { Id = "call_3", Name = "get_test", ArgumentsJson = "{}" },
                new AgentToolCall { Id = "call_4", Name = "get_test", ArgumentsJson = "{}" },
                new AgentToolCall { Id = "call_5", Name = "get_test", ArgumentsJson = "{}" }
            });
        var orchestrator = CreateOrchestrator(
            llm, executor, registry, store, usageStore: usageStore);

        var result = await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "цикл",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        Assert.Contains("loop_aborted", result.FinalText, StringComparison.Ordinal);
        Assert.False(result.MaxRoundsReached);
        Assert.Equal(1, result.Rounds);
        Assert.Equal(2, tool.Calls);
        Assert.Single(llm.Requests);

        var toolMessages = store.Messages
            .Where(message => message.Role == AgentRole.Tool)
            .ToList();
        Assert.Equal(5, toolMessages.Count);
        Assert.All(
            toolMessages.Skip(4),
            message => Assert.Contains("loop_aborted", message.Content, StringComparison.Ordinal));

        var usage = Assert.Single(usageStore.Records);
        Assert.Equal("ok", usage.Status);
    }

    [Fact]
    public async Task RunTurn_TwoIdenticalSuccessfulCalls_AreNotBlocked()
    {
        var store = new InMemoryConversationStore();
        var tool = new CountingTool();
        var registry = new AgentToolRegistry(new IAgentTool[] { tool });
        var executor = CreateExecutor(registry);
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant(
            null,
            new[]
            {
                new AgentToolCall { Id = "call_1", Name = "get_test", ArgumentsJson = "{}" },
                new AgentToolCall { Id = "call_2", Name = "get_test", ArgumentsJson = "{}" }
            });
        llm.EnqueueAssistant("done");
        var orchestrator = CreateOrchestrator(llm, executor, registry, store);

        var result = await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "повтори",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        Assert.Equal("done", result.FinalText);
        Assert.Equal(2, tool.Calls);
        Assert.DoesNotContain(
            llm.Requests[1].Messages,
            message => message.Role == AgentRole.Tool
                       && message.Content!.Contains("loop_detected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunTurn_DifferentArguments_AreNotTreatedAsALoop()
    {
        var store = new InMemoryConversationStore();
        var tool = new CountingTool();
        var registry = new AgentToolRegistry(new IAgentTool[] { tool });
        var executor = CreateExecutor(registry);
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant(
            null,
            new[]
            {
                new AgentToolCall { Id = "call_1", Name = "get_test", ArgumentsJson = "{\"round\":1}" },
                new AgentToolCall { Id = "call_2", Name = "get_test", ArgumentsJson = "{\"round\":2}" },
                new AgentToolCall { Id = "call_3", Name = "get_test", ArgumentsJson = "{\"round\":3}" }
            });
        llm.EnqueueAssistant("done");
        var orchestrator = CreateOrchestrator(llm, executor, registry, store);

        var result = await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "разные аргументы",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        Assert.Equal(3, tool.Calls);
        Assert.Equal(3, result.ToolCallCount);
    }

    [Fact]
    public async Task RunTurn_TwoFailuresOfTheSameCall_AddAWarningToTheResult()
    {
        var store = new InMemoryConversationStore();
        var tool = new CountingTool(ToolResult.Fail("boom", "Tool exploded"));
        var registry = new AgentToolRegistry(new IAgentTool[] { tool });
        var executor = CreateExecutor(registry);
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant(
            null,
            new[]
            {
                new AgentToolCall { Id = "call_1", Name = "get_test", ArgumentsJson = "{}" },
                new AgentToolCall { Id = "call_2", Name = "get_test", ArgumentsJson = "{}" }
            });
        llm.EnqueueAssistant("не удалось");
        var orchestrator = CreateOrchestrator(llm, executor, registry, store);

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "две ошибки",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        var toolMessages = llm.Requests[1].Messages
            .Where(message => message.Role == AgentRole.Tool)
            .ToList();
        Assert.Equal(2, toolMessages.Count);
        Assert.DoesNotContain("failed twice", toolMessages[0].Content, StringComparison.Ordinal);
        Assert.Contains("failed twice", toolMessages[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTurn_RepairedArguments_ArePersistedInToolCallTelemetry()
    {
        var store = new InMemoryConversationStore();
        var tool = new ScriptedTool("write_tool");
        var registry = new AgentToolRegistry(new IAgentTool[] { tool });
        var executor = CreateExecutor(registry);
        var telemetry = new CapturingToolCallStore();
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant(
            null,
            new[]
            {
                new AgentToolCall
                {
                    Id = "call_1",
                    Name = "write_tool",
                    ArgumentsJson = "{\"view_id\":5}"
                }
            });
        llm.EnqueueAssistant("done");
        var orchestrator = CreateOrchestrator(
            llm, executor, registry, store, toolCallStore: telemetry);

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "переименуй",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        var record = Assert.Single(telemetry.Records);
        Assert.NotNull(record.RepairsJson);
        Assert.Contains("renamed 'view_id' -> 'viewId'", record.RepairsJson, StringComparison.Ordinal);
        Assert.Contains("viewId", tool.Executions.Single().ArgumentsJson, StringComparison.Ordinal);
    }
}
