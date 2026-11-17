using Xunit;
using System.Text.Json;
using SmartCon.Agent.Services;
using SmartCon.Agent.Services.Llm;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Tests.Agent.Services;

public sealed class AgentOrchestratorTests
{
    [Fact]
    public async Task RunTurn_ToolCallThenAnswer_ExecutesToolAndAppendsMessages()
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
                    ArgumentsJson = "{}"
                }
            },
            "thinking");
        llm.EnqueueAssistant("Готово");

        var orchestrator = CreateOrchestrator(llm, executor, registry, store);

        var result = await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "привет",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        Assert.Equal("Готово", result.FinalText);
        Assert.Equal(1, result.ToolCallCount);
        Assert.Equal(1, tool.Calls);
        Assert.Equal(4, store.Messages.Count);
        Assert.Equal(2, llm.Requests.Count);

        var secondRequest = llm.Requests[1].Messages;
        Assert.Contains(secondRequest, message =>
            message.Role == AgentRole.Tool && message.ToolCallId == "call_1");

        var replayedAssistant = secondRequest.First(message => message.Role == AgentRole.Assistant);
        Assert.Equal("thinking", replayedAssistant.ReasoningContent);
        Assert.Single(replayedAssistant.ToolCalls);
    }

    [Fact]
    public async Task RunTurn_CancelledToken_Throws()
    {
        var store = new InMemoryConversationStore();
        var tool = new CountingTool();
        var registry = new AgentToolRegistry(new IAgentTool[] { tool });
        var executor = CreateExecutor(registry);
        var orchestrator = CreateOrchestrator(
            new FakeAgentLlmClient(),
            executor,
            registry,
            store);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            orchestrator.RunTurnAsync(
                Guid.NewGuid(),
                "привет",
                Array.Empty<AgentAttachment>(),
                null,
                cts.Token));
    }

    [Fact]
    public async Task RunTurn_ToolFailure_IsReportedBackToTheModel()
    {
        var store = new InMemoryConversationStore();
        var tool = new CountingTool(ToolResult.Fail("boom", "Tool exploded"));
        var registry = new AgentToolRegistry(new IAgentTool[] { tool });
        var executor = CreateExecutor(registry);

        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant(
            null,
            new[] { new AgentToolCall { Id = "call_1", Name = "get_test", ArgumentsJson = "{}" } });
        llm.EnqueueAssistant("Не удалось");

        var orchestrator = CreateOrchestrator(llm, executor, registry, store);

        var result = await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "привет",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        Assert.Equal("Не удалось", result.FinalText);
        var toolMessage = llm.Requests[1].Messages.Last(message => message.Role == AgentRole.Tool);
        Assert.NotNull(toolMessage.Content);
        Assert.Contains("boom", toolMessage.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTurn_HistoryWithOrphanedToolCall_BackfillsCancelledResult()
    {
        var store = new InMemoryConversationStore();
        store.Messages.Add(AgentMessage.Assistant(
            null,
            new[]
            {
                new AgentToolCall
                {
                    Id = "lost_call",
                    Name = "list_views",
                    ArgumentsJson = "{}"
                }
            },
            "thinking"));
        store.Messages.Add(AgentMessage.User("продолжай"));

        var registry = new AgentToolRegistry(Array.Empty<IAgentTool>());
        var executor = CreateExecutor(registry);
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant("Ок");
        var orchestrator = CreateOrchestrator(llm, executor, registry, store);

        var result = await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "продолжай",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        Assert.Equal("Ок", result.FinalText);
        var replayed = llm.Requests[0].Messages;
        var toolMessage = Assert.Single(
            replayed,
            message => message.Role == AgentRole.Tool && message.ToolCallId == "lost_call");
        Assert.NotNull(toolMessage.Content);
        Assert.Contains("cancelled", toolMessage.Content, StringComparison.Ordinal);
    }

    private static AgentToolExecutor CreateExecutor(IToolRegistry registry)
    {
        return new AgentToolExecutor(
            registry,
            new ImmediateAwaitableEvent(),
            new AgentSessionContext(),
            new AutoApproveConfirmation());
    }

    private sealed class AutoApproveConfirmation : IAgentConfirmationService
    {
        public Task<AgentConfirmationResult> RequestAsync(
            AgentConfirmationRequest request,
            CancellationToken ct) =>
            Task.FromResult(AgentConfirmationResult.Approved);
    }

    private static AgentOrchestrator CreateOrchestrator(
        FakeAgentLlmClient llm,
        IToolExecutor executor,
        IToolRegistry registry,
        IAgentConversationStore store)
    {
        return new AgentOrchestrator(
            llm,
            executor,
            registry,
            store,
            new NullAuditLog(),
            new StubProjectContextProvider(),
            new AgentLlmSettings { Model = "test-model" },
            new AgentSessionContext(),
            new AgentPromptBuilder());
    }

    private sealed class CountingTool : IAgentTool
    {
        private readonly ToolResult _result;

        public CountingTool(ToolResult? result = null)
        {
            _result = result ?? ToolResult.Ok(null, "ok");
        }

        public int Calls { get; private set; }

        public AgentToolDefinition Definition { get; } = new()
        {
            Name = "get_test",
            Description = "test tool",
            Risk = AgentToolRisk.Read,
            InputSchemaJson = "{\"type\":\"object\",\"properties\":{}}"
        };

        public Task<ToolResult> ExecuteAsync(
            AgentToolContext context,
            JsonElement args,
            CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }

    private sealed class ImmediateAwaitableEvent : IAgentAwaitableEvent
    {
        public Task<T> RaiseAsync<T>(Func<object, T> funcWithApp, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(funcWithApp(new object()));
        }

        public Task RaiseAsync(Action<object> actionWithApp, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            actionWithApp(new object());
            return Task.CompletedTask;
        }

        public void ProcessQueue(object revitApp)
        {
        }

        public void Initialize(Action onRaise)
        {
        }
    }

    private sealed class StubProjectContextProvider : IAgentProjectContextProvider
    {
        public Task<AgentProjectContext> GetAsync(CancellationToken ct) => Task.FromResult(
            new AgentProjectContext
            {
                DocumentId = "doc",
                Title = "Test document",
                RevitVersion = "2025"
            });
    }

    private sealed class NullAuditLog : IAgentAuditLog
    {
        public Task WriteAsync(AgentAuditEntry entry, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class InMemoryConversationStore : IAgentConversationStore
    {
        public List<AgentMessage> Messages { get; } = new();

        public Task<Guid> CreateConversationAsync(AgentConversationHeader header, CancellationToken ct) =>
            Task.FromResult(Guid.NewGuid());

        public Task<IReadOnlyList<AgentConversationHeader>> ListConversationsAsync(
            string documentId,
            int limit,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AgentConversationHeader>>(
                Array.Empty<AgentConversationHeader>());

        public Task<AgentConversationHeader?> GetConversationAsync(
            Guid conversationId,
            CancellationToken ct) =>
            Task.FromResult<AgentConversationHeader?>(null);

        public Task AppendMessageAsync(Guid conversationId, AgentMessage message, CancellationToken ct)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AgentMessage>> GetMessagesAsync(
            Guid conversationId,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AgentMessage>>(Messages.ToList());

        public Task UpdateTitleAsync(Guid conversationId, string title, CancellationToken ct) =>
            Task.CompletedTask;

        public Task DeleteConversationAsync(Guid conversationId, CancellationToken ct) =>
            Task.CompletedTask;
    }
}