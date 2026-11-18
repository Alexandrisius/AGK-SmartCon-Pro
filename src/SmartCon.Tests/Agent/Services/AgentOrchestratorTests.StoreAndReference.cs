using System.Text.Json;
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
    public async Task RunTurn_TrimmedResult_IsStoredAndCarriesItsHandle()
    {
        var store = new InMemoryConversationStore();
        var blobStore = new RecordingBlobStore();
        var tool = new ScriptedTool(
            "get_test",
            AgentToolRisk.Read,
            supportsDryRun: false,
            handler: _ => ToolResult.Ok(
                JsonSerializer.SerializeToElement(new
                {
                    items = Enumerable.Range(0, 5000)
                        .Select(index => new { id = index, name = "План " + index })
                        .ToArray()
                }),
                "ok"));
        var registry = new AgentToolRegistry(new IAgentTool[] { tool });
        var executor = CreateExecutor(registry);
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant(
            null,
            new[] { new AgentToolCall { Id = "call_big", Name = "get_test", ArgumentsJson = "{}" } });
        llm.EnqueueAssistant("done");
        var orchestrator = CreateOrchestrator(
            llm,
            executor,
            registry,
            store,
            settings: new AgentLlmSettings { Model = "test-model" },
            toolResultBlobStore: blobStore);

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "покажи всё",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        var saved = Assert.Single(blobStore.Saved);
        Assert.Equal("call_big", saved.Handle);
        Assert.True(saved.Json.Length > ToolResultBudget.DefaultMaxCharacters);

        var toolMessage = llm.Requests[1].Messages.Last(message => message.Role == AgentRole.Tool);
        Assert.Contains("\"resultHandle\":\"call_big\"", toolMessage.Content, StringComparison.Ordinal);
        Assert.Contains("read_tool_result", toolMessage.Content, StringComparison.Ordinal);
        Assert.True(toolMessage.Content!.Length <= ToolResultBudget.DefaultMaxCharacters);
    }

    [Fact]
    public async Task RunTurn_SmallResult_IsNotStored()
    {
        var store = new InMemoryConversationStore();
        var blobStore = new RecordingBlobStore();
        var tool = new ScriptedTool(
            "get_test",
            AgentToolRisk.Read,
            supportsDryRun: false,
            handler: _ => ToolResult.Ok(
                JsonSerializer.SerializeToElement(new { items = new[] { 1, 2, 3 } }),
                "ok"));
        var registry = new AgentToolRegistry(new IAgentTool[] { tool });
        var executor = CreateExecutor(registry);
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant(
            null,
            new[] { new AgentToolCall { Id = "call_small", Name = "get_test", ArgumentsJson = "{}" } });
        llm.EnqueueAssistant("done");
        var orchestrator = CreateOrchestrator(
            llm,
            executor,
            registry,
            store,
            settings: new AgentLlmSettings { Model = "test-model" },
            toolResultBlobStore: blobStore);

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "покажи немного",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        Assert.Empty(blobStore.Saved);
        var toolMessage = llm.Requests[1].Messages.Last(message => message.Role == AgentRole.Tool);
        Assert.DoesNotContain("resultHandle", toolMessage.Content, StringComparison.Ordinal);
    }

    private sealed class RecordingBlobStore : IAgentToolResultBlobStore
    {
        public List<(Guid SessionId, string Handle, string Json)> Saved { get; } = new();

        public Task SaveAsync(
            Guid sessionId,
            string toolCallId,
            string resultJson,
            CancellationToken ct)
        {
            Saved.Add((sessionId, toolCallId, resultJson));
            return Task.CompletedTask;
        }

        public Task<AgentToolResultBlob?> GetAsync(
            Guid sessionId,
            string toolCallId,
            CancellationToken ct) =>
            Task.FromResult<AgentToolResultBlob?>(null);
    }
}
