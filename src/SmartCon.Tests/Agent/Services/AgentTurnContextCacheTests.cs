using SmartCon.Agent.Services;
using SmartCon.Agent.Services.Llm;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// P.2: the turn context is an append-only system message, the user message is
/// raw, and the per-round status is the only ephemeral tail message.
/// </summary>
public sealed class AgentTurnContextCacheTests
{
    private const string TurnContextMarker = "<turn_context>";
    private const string ContextStatusMarker = "<context_status";

    [Fact]
    public async Task ThreeTurns_UserMessagesStayRawAndHistoryIsAppendOnly()
    {
        var registry = new AgentToolRegistry(Array.Empty<IAgentTool>());
        var store = new InMemoryConversationStore();
        var llm = new FakeAgentLlmClient();
        var orchestrator = AgentHarness.Create(llm, registry, store);
        var conversationId = Guid.NewGuid();
        var texts = new[] { "переименуй виды", "продолжай", "покажи листы" };

        foreach (var text in texts)
            await RunTurn(llm, orchestrator, conversationId, text);

        Assert.Equal(texts.Length, llm.Requests.Count);
        for (var index = 0; index < texts.Length; index++)
        {
            var messages = llm.Requests[index].Messages;
            var user = messages.Last(message => message.Role == AgentRole.User);
            Assert.Equal(texts[index], user.Content);
            Assert.DoesNotContain(messages, message =>
                (message.Content ?? string.Empty).Contains("<progress", StringComparison.Ordinal));
            Assert.Equal(1, messages.Count(message =>
                (message.Content ?? string.Empty).Contains(
                    ContextStatusMarker, StringComparison.Ordinal)));
            Assert.Equal(AgentRole.System, messages[^1].Role);
            Assert.StartsWith(ContextStatusMarker, messages[^1].Content);
        }

        for (var index = 1; index < llm.Requests.Count; index++)
        {
            var previous = WithoutTrailingStatus(llm.Requests[index - 1].Messages);
            var current = WithoutTrailingStatus(llm.Requests[index].Messages);
            Assert.True(
                current.Take(previous.Count).SequenceEqual(previous, new MessageContentComparer()),
                "turn " + (index + 1) + " must extend the previous request instead of rewriting it");
        }

        Assert.Contains(
            store.Messages,
            message => message.Role == AgentRole.User && message.Content == texts[0]);
        Assert.DoesNotContain(
            store.Messages,
            message => (message.Content ?? string.Empty).Contains("<progress", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TurnContext_IsPersistedOncePerTurn_NotPerRound()
    {
        var tool = new ScriptedTool("get_project_overview", AgentToolRisk.Read);
        var registry = new AgentToolRegistry(new IAgentTool[] { tool });
        var store = new InMemoryConversationStore();
        var llm = new FakeAgentLlmClient();
        var orchestrator = AgentHarness.Create(llm, registry, store);

        llm.EnqueueAssistant(
            null,
            new[]
            {
                new AgentToolCall
                {
                    Id = "call_1",
                    Name = "get_project_overview",
                    ArgumentsJson = "{}"
                }
            });
        llm.EnqueueAssistant("ответ");

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "переименуй виды",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        Assert.Equal(2, llm.Requests.Count);
        foreach (var request in llm.Requests)
        {
            Assert.Equal(1, request.Messages.Count(message =>
                (message.Content ?? string.Empty).StartsWith(
                    TurnContextMarker, StringComparison.Ordinal)));
            Assert.Equal(1, request.Messages.Count(message =>
                (message.Content ?? string.Empty).Contains(
                    ContextStatusMarker, StringComparison.Ordinal)));
        }

        Assert.Equal(1, store.Messages.Count(message =>
            (message.Content ?? string.Empty).StartsWith(TurnContextMarker, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task SecondTurn_AppendsANewTurnContextWithoutTouchingTheFirst()
    {
        var registry = new AgentToolRegistry(Array.Empty<IAgentTool>());
        var store = new InMemoryConversationStore();
        var llm = new FakeAgentLlmClient();
        var orchestrator = AgentHarness.Create(llm, registry, store);
        var conversationId = Guid.NewGuid();

        await RunTurn(llm, orchestrator, conversationId, "первый запрос");
        var firstRequest = WithoutTrailingStatus(llm.Requests[0].Messages);

        await RunTurn(llm, orchestrator, conversationId, "второй запрос");

        var secondRequest = llm.Requests[1].Messages;
        Assert.True(secondRequest.Take(firstRequest.Count).SequenceEqual(
            firstRequest,
            new MessageContentComparer()));
        Assert.Equal(2, secondRequest.Count(message =>
            (message.Content ?? string.Empty).StartsWith(
                TurnContextMarker, StringComparison.Ordinal)));
        Assert.Equal(2, store.Messages.Count(message =>
            (message.Content ?? string.Empty).StartsWith(TurnContextMarker, StringComparison.Ordinal)));
    }

    private static Task RunTurn(
        FakeAgentLlmClient llm,
        AgentOrchestrator orchestrator,
        Guid conversationId,
        string text)
    {
        llm.EnqueueAssistant("ответ");
        return orchestrator.RunTurnAsync(
            conversationId,
            text,
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);
    }

    private static List<AgentMessage> WithoutTrailingStatus(IReadOnlyList<AgentMessage> messages)
    {
        var list = messages.ToList();
        if (list.Count > 0
            && (list[^1].Content ?? string.Empty).StartsWith(
                ContextStatusMarker, StringComparison.Ordinal))
        {
            list.RemoveAt(list.Count - 1);
        }

        return list;
    }

    private sealed class MessageContentComparer : IEqualityComparer<AgentMessage>
    {
        public bool Equals(AgentMessage? x, AgentMessage? y) =>
            x is not null && y is not null
            && x.Role == y.Role
            && string.Equals(x.Content, y.Content, StringComparison.Ordinal)
            && string.Equals(x.ToolCallId, y.ToolCallId, StringComparison.Ordinal);

        public int GetHashCode(AgentMessage obj) => HashCode.Combine(obj.Role, obj.Content);
    }
}