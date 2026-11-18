using Xunit;
using SmartCon.Agent.Services.Llm;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Tests.Agent.Services;

public sealed partial class AgentOrchestratorTests
{
    [Fact]
    public async Task RunTurn_ToolCallTelemetry_RedactsCredentials()
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
                new AgentToolCall
                {
                    Id = "call_1",
                    Name = "get_test",
                    ArgumentsJson = "{\"api_key\":\"sk-secret12345678\"}"
                }
            });
        llm.EnqueueAssistant("done");
        var telemetry = new CapturingToolCallStore();

        var orchestrator = CreateOrchestrator(
            llm, executor, registry, store, toolCallStore: telemetry);

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "привет",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        var record = Assert.Single(telemetry.Records);
        Assert.DoesNotContain("sk-secret12345678", record.ArgumentsJson);
        Assert.Contains(AgentRedactor.Replacement, record.ArgumentsJson);
    }

    [Fact]
    public async Task RunTurn_Success_WritesOkUsageWithLatencyAndRequestId()
    {
        var conversationId = Guid.NewGuid();
        var store = new InMemoryConversationStore();
        var registry = new AgentToolRegistry(Array.Empty<IAgentTool>());
        var executor = CreateExecutor(registry);
        var llm = new FakeAgentLlmClient();
        llm.Enqueue(new AgentChatResponse
        {
            Message = AgentMessage.Assistant("done"),
            Usage = new AgentUsage
            {
                PromptTokens = 150,
                CompletionTokens = 20,
                TotalTokens = 170
            },
            Model = "fake-model",
            Provider = "fake",
            RequestId = "req-42"
        });
        var usageStore = new CapturingUsageStore();
        var orchestrator = CreateOrchestrator(
            llm, executor, registry, store, usageStore: usageStore);

        await orchestrator.RunTurnAsync(
            conversationId,
            "привет",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        var record = Assert.Single(usageStore.Records);
        Assert.Equal("ok", record.Status);
        Assert.Equal(conversationId.ToString("D"), record.SessionId);
        Assert.Equal("fake", record.Provider);
        Assert.Equal("fake-model", record.Model);
        Assert.Equal(150, record.Usage.PromptTokens);
        Assert.Equal("req-42", record.RequestId);
        Assert.NotNull(record.LatencyMs);
        Assert.True(record.LatencyMs!.Value >= 0);
        Assert.True(record.EstimatedPromptTokens > 0);
    }

    [Fact]
    public async Task RunTurn_LlmFailure_WritesErrorUsage()
    {
        var store = new InMemoryConversationStore();
        var registry = new AgentToolRegistry(Array.Empty<IAgentTool>());
        var executor = CreateExecutor(registry);
        var usageStore = new CapturingUsageStore();
        var orchestrator = CreateOrchestrator(
            new FakeAgentLlmClient(), executor, registry, store, usageStore: usageStore);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            orchestrator.RunTurnAsync(
                Guid.NewGuid(),
                "привет",
                Array.Empty<AgentAttachment>(),
                null,
                CancellationToken.None));

        var record = Assert.Single(usageStore.Records);
        Assert.Equal("error", record.Status);
        Assert.Null(record.RequestId);
    }

    [Fact]
    public async Task RunTurn_Cancelled_WritesCancelledUsage()
    {
        var store = new InMemoryConversationStore();
        var registry = new AgentToolRegistry(Array.Empty<IAgentTool>());
        var executor = CreateExecutor(registry);
        var usageStore = new CapturingUsageStore();
        var orchestrator = CreateOrchestrator(
            new FakeAgentLlmClient(), executor, registry, store, usageStore: usageStore);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            orchestrator.RunTurnAsync(
                Guid.NewGuid(),
                "привет",
                Array.Empty<AgentAttachment>(),
                null,
                cts.Token));

        var record = Assert.Single(usageStore.Records);
        Assert.Equal("cancelled", record.Status);
    }

    [Fact]
    public async Task RunTurn_ProviderUsage_UpdatesSessionCalibration()
    {
        var store = new InMemoryConversationStore();
        var registry = new AgentToolRegistry(Array.Empty<IAgentTool>());
        var executor = CreateExecutor(registry);
        var session = new AgentSessionContext();
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant(
            "done",
            usage: new AgentUsage
            {
                PromptTokens = 400,
                CompletionTokens = 10,
                TotalTokens = 410
            });
        var usageStore = new CapturingUsageStore();
        var orchestrator = CreateOrchestrator(
            llm, executor, registry, store, session, usageStore: usageStore);

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "привет",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        Assert.Equal(1, session.TokenCalibration.Samples);
        Assert.InRange(session.TokenCalibration.Factor, 0.5, 2.0);
        Assert.True(Assert.Single(usageStore.Records).EstimatedPromptTokens > 0);
    }

    [Fact]
    public async Task RunTurn_MultiRoundSuccess_WritesUsageExactlyOnce()
    {
        var store = new InMemoryConversationStore();
        var tool = new CountingTool();
        var registry = new AgentToolRegistry(new IAgentTool[] { tool });
        var executor = CreateExecutor(registry);
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant(
            null,
            new[] { new AgentToolCall { Id = "call_1", Name = "get_test", ArgumentsJson = "{}" } },
            usage: new AgentUsage { PromptTokens = 100, CompletionTokens = 5, TotalTokens = 105 });
        llm.EnqueueAssistant(
            "done",
            usage: new AgentUsage { PromptTokens = 60, CompletionTokens = 5, TotalTokens = 65 });
        var usageStore = new CapturingUsageStore();
        var orchestrator = CreateOrchestrator(
            llm, executor, registry, store, usageStore: usageStore);

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "привет",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        var record = Assert.Single(usageStore.Records);
        Assert.Equal("ok", record.Status);
        Assert.Equal(160, record.Usage.PromptTokens);
        Assert.Equal(2, llm.Requests.Count);
    }

    [Fact]
    public async Task RunTurn_MaxRounds_WritesUsageExactlyOnce()
    {
        var store = new InMemoryConversationStore();
        var tool = new CountingTool();
        var registry = new AgentToolRegistry(new IAgentTool[] { tool });
        var executor = CreateExecutor(registry);
        var llm = new FakeAgentLlmClient();
        for (var round = 0; round < 8; round++)
        {
            llm.EnqueueAssistant(
                null,
                new[]
                {
                    new AgentToolCall
                    {
                        Id = "call_" + round,
                        Name = "get_test",
                        ArgumentsJson = "{}"
                    }
                },
                usage: new AgentUsage { PromptTokens = 10, CompletionTokens = 1, TotalTokens = 11 });
        }

        var usageStore = new CapturingUsageStore();
        var orchestrator = CreateOrchestrator(
            llm, executor, registry, store, usageStore: usageStore);

        var result = await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "привет",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        Assert.True(result.MaxRoundsReached);
        Assert.Equal(8, llm.Requests.Count);
        var record = Assert.Single(usageStore.Records);
        Assert.Equal("ok", record.Status);
        Assert.Equal(80, record.Usage.PromptTokens);
    }
}
