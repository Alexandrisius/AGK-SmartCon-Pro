using SmartCon.Agent.Services;
using SmartCon.Agent.Services.Llm;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;

namespace SmartCon.Tests.Agent.Support;

/// <summary>Project context provider whose answer can change between turns.</summary>
internal sealed class MutableProjectContextProvider : IAgentProjectContextProvider
{
    public AgentProjectContext Context { get; set; } = new()
    {
        DocumentId = "doc-a",
        Title = "Project A",
        RevitVersion = "2025",
        Units = "mm",
        IsWorkshared = false
    };

    public int Calls { get; private set; }

    public Task<AgentProjectContext> GetAsync(CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(Context);
    }
}

internal sealed class InMemoryConversationStore : IAgentConversationStore
{
    public List<AgentMessage> Messages { get; } = new();

    public Task<Guid> CreateConversationAsync(
        AgentConversationHeader header,
        CancellationToken ct) =>
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

    public Task AppendMessageAsync(
        Guid conversationId,
        AgentMessage message,
        CancellationToken ct)
    {
        Messages.Add(message.SessionIndex > 0
            ? message
            : message.WithSessionIndex(Messages.Count + 1));
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

internal sealed class CapturingUsageStore : IAgentUsageStore
{
    public List<AgentUsageRecord> Records { get; } = new();

    public Task RecordAsync(AgentUsageRecord record, CancellationToken ct)
    {
        Records.Add(record);
        return Task.CompletedTask;
    }

    public Task<decimal> GetSessionCostAsync(Guid sessionId, CancellationToken ct) =>
        Task.FromResult(0m);

    public Task<AgentContextSnapshot?> GetLastContextAsync(
        Guid sessionId,
        CancellationToken ct) =>
        Task.FromResult<AgentContextSnapshot?>(null);
}

/// <summary>
/// Builds the production orchestrator over in-memory stores so prompt-layout and
/// cache tests can drive several turns without Revit.
/// </summary>
internal static class AgentHarness
{
    public static AgentOrchestrator Create(
        IAgentLlmClient llm,
        IToolRegistry registry,
        InMemoryConversationStore store,
        AgentSessionContext? session = null,
        IAgentProjectContextProvider? projectContext = null,
        IAgentUsageStore? usageStore = null,
        IAgentContextCompactor? contextCompactor = null,
        AgentLlmSettings? settings = null)
    {
        var effectiveSession = session ?? new AgentSessionContext();
        var executor = new AgentToolExecutor(
            registry,
            new ImmediateAwaitableEvent(),
            effectiveSession,
            new RecordingConfirmation());

        return new AgentOrchestrator(
            llm,
            executor,
            registry,
            store,
            new RecordingAuditLog(),
            projectContext ?? new MutableProjectContextProvider(),
            settings ?? new AgentLlmSettings { Model = "test-model" },
            effectiveSession,
            new AgentPromptBuilder(),
            usageStore: usageStore,
            contextCompactor: contextCompactor);
    }
}