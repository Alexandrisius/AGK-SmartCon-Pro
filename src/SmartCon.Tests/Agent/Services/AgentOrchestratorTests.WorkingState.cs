using Xunit;
using System.Text.Json;
using SmartCon.Agent.Services.Llm;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Tests.Agent.Services;

public sealed partial class AgentOrchestratorTests
{
    [Fact]
    public async Task RunTurn_PersistsTurnContextAsASeparateMessageAndKeepsUserTextRaw()
    {
        const string userText = "Переименуй виды, не трогай листы";
        var projectContext = new StubProjectContextProvider("doc-ws");

        var store = new InMemoryConversationStore();
        var registry = new AgentToolRegistry(Array.Empty<IAgentTool>());
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant("ok");
        var workingStore = new InMemoryWorkingStateStore();
        var orchestrator = CreateOrchestrator(
            llm,
            CreateExecutor(registry),
            registry,
            store,
            projectContext: projectContext,
            settings: new AgentLlmSettings { Model = "test-model" },
            workingStateStore: workingStore);

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            userText,
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        var request = llm.Requests[0].Messages;
        var user = request.Last(message => message.Role == AgentRole.User);
        Assert.Equal(userText, user.Content);

        var turnContext = Assert.Single(request, message =>
            message.Role == AgentRole.System
            && (message.Content ?? string.Empty).StartsWith("<turn_context>", StringComparison.Ordinal));
        Assert.Contains("goal: " + userText, turnContext.Content);
        var ordered = request.ToList();
        Assert.True(
            ordered.IndexOf(turnContext) < ordered.IndexOf(user),
            "the turn context must precede the user message it describes");

        Assert.DoesNotContain(request, message =>
            (message.Content ?? string.Empty).Contains("<progress", StringComparison.Ordinal));

        Assert.Contains(
            store.Messages,
            message => message.Role == AgentRole.User && message.Content == userText);
        Assert.DoesNotContain(
            store.Messages,
            message => message.Role == AgentRole.User
                && (message.Content ?? string.Empty).Contains(
                    "<turn_context>", StringComparison.Ordinal));
        Assert.Contains(
            store.Messages,
            message => message.Role == AgentRole.System
                && (message.Content ?? string.Empty).StartsWith(
                    "<turn_context>", StringComparison.Ordinal));

        var saved = Assert.Single(workingStore.Saved);
        Assert.Equal(userText, saved.Goal);
    }

    [Fact]
    public async Task RunTurn_RestoresGoalFromStoreOnRestart()
    {
        var conversationId = Guid.NewGuid();
        var conversationStore = new InMemoryConversationStore();
        var workingStore = new InMemoryWorkingStateStore();
        var projectContext = new StubProjectContextProvider("doc-ws");
        var settings = new AgentLlmSettings { Model = "test-model" };

        var firstRegistry = new AgentToolRegistry(Array.Empty<IAgentTool>());
        var firstLlm = new FakeAgentLlmClient();
        firstLlm.EnqueueAssistant("ok");
        var first = CreateOrchestrator(
            firstLlm,
            CreateExecutor(firstRegistry),
            firstRegistry,
            conversationStore,
            projectContext: projectContext,
            settings: settings,
            workingStateStore: workingStore);

        await first.RunTurnAsync(
            conversationId,
            "Почини имена видов",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        var secondRegistry = new AgentToolRegistry(Array.Empty<IAgentTool>());
        var secondLlm = new FakeAgentLlmClient();
        secondLlm.EnqueueAssistant("ok");
        var second = CreateOrchestrator(
            secondLlm,
            CreateExecutor(secondRegistry),
            secondRegistry,
            conversationStore,
            projectContext: projectContext,
            settings: settings,
            workingStateStore: workingStore);

        await second.RunTurnAsync(
            conversationId,
            "продолжай",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        var user = secondLlm.Requests[0].Messages.Last(
            message => message.Role == AgentRole.User);
        Assert.Equal("продолжай", user.Content);

        var turnContext = secondLlm.Requests[0].Messages.Last(message =>
            message.Role == AgentRole.System
            && (message.Content ?? string.Empty).StartsWith("<turn_context>", StringComparison.Ordinal));
        Assert.Contains("goal: Почини имена видов", turnContext.Content);
    }

    [Fact]
    public async Task RunTurn_RecordsChangeSetFromToolResult()
    {
        var store = new InMemoryConversationStore();
        var payload = JsonSerializer.SerializeToElement(new
        {
            changeSetId = "cs-42",
            changed = new[] { new { id = 7 } }
        });
        var tool = new CountingTool(ToolResult.Ok(payload, "applied", 1));
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
            });
        llm.EnqueueAssistant("done");
        var workingStore = new InMemoryWorkingStateStore();

        var orchestrator = CreateOrchestrator(
            llm,
            executor,
            registry,
            store,
            settings: new AgentLlmSettings { Model = "test-model" },
            workingStateStore: workingStore);

        await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "примени",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        var saved = Assert.Single(workingStore.Saved);
        Assert.Equal(new[] { "cs-42" }, saved.ChangeSetIds);
        Assert.Equal(new[] { "7" }, saved.TouchedElementIds);
    }

    [Fact]
    public async Task RunTurn_ConversationChanged_ResetsWorkingStateAndDoesNotLeakOldGoal()
    {
        var firstConversation = Guid.NewGuid();
        var secondConversation = Guid.NewGuid();
        var conversationStore = new InMemoryConversationStore();
        var workingStore = new InMemoryWorkingStateStore();
        var session = new AgentSessionContext();
        var settings = new AgentLlmSettings { Model = "test-model" };
        var registry = new AgentToolRegistry(Array.Empty<IAgentTool>());
        var executor = CreateExecutor(registry);
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant("ok");
        llm.EnqueueAssistant("ok");
        var orchestrator = CreateOrchestrator(
            llm,
            executor,
            registry,
            conversationStore,
            session,
            settings: settings,
            workingStateStore: workingStore);

        await orchestrator.RunTurnAsync(
            firstConversation,
            "Почини имена видов",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        await orchestrator.RunTurnAsync(
            secondConversation,
            "Другая задача",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        var secondUser = llm.Requests[1].Messages.Last(
            message => message.Role == AgentRole.User);
        Assert.Equal("Другая задача", secondUser.Content);

        var secondTurnContext = llm.Requests[1].Messages.Last(message =>
            message.Role == AgentRole.System
            && (message.Content ?? string.Empty).StartsWith("<turn_context>", StringComparison.Ordinal));
        Assert.Contains("goal: Другая задача", secondTurnContext.Content);
        Assert.DoesNotContain("Почини имена видов", secondTurnContext.Content);

        Assert.Equal("Другая задача", session.WorkingState!.Goal);
        Assert.Equal(2, workingStore.Saved.Count);
        Assert.Equal("Почини имена видов", workingStore.Saved[0].Goal);
        Assert.Equal("Другая задача", workingStore.Saved[1].Goal);
    }
}
