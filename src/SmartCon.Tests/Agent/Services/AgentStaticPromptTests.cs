using SmartCon.Agent.Services;
using SmartCon.Agent.Services.Llm;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// P.3: the system prefix is static; session baseline is written once per
/// document; view, mode, plan and counters travel in the turn-context tail.
/// </summary>
public sealed class AgentStaticPromptTests
{
    [Fact]
    public async Task StaticPrompt_StaysIdenticalAcrossFiveTurnsWithChangingViewAndMode()
    {
        var registry = new AgentToolRegistry(Array.Empty<IAgentTool>());
        var store = new InMemoryConversationStore();
        var llm = new FakeAgentLlmClient();
        var session = new AgentSessionContext();
        var provider = new MutableProjectContextProvider();
        var orchestrator = AgentHarness.Create(
            llm, registry, store, session: session, projectContext: provider);
        var conversationId = Guid.NewGuid();
        var modes = new[]
        {
            AgentApprovalMode.ConfirmWrites,
            AgentApprovalMode.ReadOnly,
            AgentApprovalMode.ReadOnly,
            AgentApprovalMode.AutoApproveSession,
            AgentApprovalMode.ConfirmWrites
        };

        for (var turn = 0; turn < modes.Length; turn++)
        {
            session.ApprovalMode = modes[turn];
            provider.Context = Context(
                title: "Project A",
                viewName: "План " + turn,
                viewId: turn + 1,
                viewCount: 100 + turn);

            llm.EnqueueAssistant("ответ " + turn);
            await orchestrator.RunTurnAsync(
                conversationId,
                "запрос " + turn,
                Array.Empty<AgentAttachment>(),
                null,
                CancellationToken.None);
        }

        var staticPrompt = llm.Requests[0].Messages[0].Content;
        Assert.NotNull(staticPrompt);
        var staticHash = AgentPromptHash.ComputeStatic(staticPrompt!);

        for (var turn = 1; turn < llm.Requests.Count; turn++)
        {
            var request = llm.Requests[turn];
            Assert.Equal(AgentRole.System, request.Messages[0].Role);
            Assert.Equal(staticPrompt, request.Messages[0].Content);
            Assert.Equal(staticHash, AgentPromptHash.ComputeStatic(request.Messages[0].Content!));

            var turnContext = request.Messages.Last(message =>
                message.Role == AgentRole.System
                && (message.Content ?? string.Empty).StartsWith(
                    "<turn_context>", StringComparison.Ordinal));
            Assert.Contains("mode: " + modes[turn], turnContext.Content, StringComparison.Ordinal);
            Assert.Contains("current_view: План " + turn, turnContext.Content, StringComparison.Ordinal);
        }

        Assert.Equal(1, store.Messages.Count(message =>
            (message.Content ?? string.Empty).StartsWith(
                AgentSystemPrompt.SessionContextHeader, StringComparison.Ordinal)));
        Assert.Equal(5, store.Messages.Count(message =>
            (message.Content ?? string.Empty).StartsWith(
                "<turn_context>", StringComparison.Ordinal)));
    }

    [Fact]
    public void StaticPrompt_ContainsNoDocumentViewModeOrCounterState()
    {
        var staticPrompt = AgentSystemPrompt.BuildStatic();

        Assert.DoesNotContain("## Current Revit document", staticPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("<session_context>", staticPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("<turn_context>", staticPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("## Approval mode", staticPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("modified:", staticPrompt, StringComparison.Ordinal);
        // The word itself may appear in a rule (write tools refuse workshared
        // documents); what must not leak is the per-document state line.
        Assert.DoesNotContain("workshared:", staticPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("workshared: yes", staticPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Active view", staticPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionContext_IsRewrittenOnlyWhenTheDocumentChanges()
    {
        var registry = new AgentToolRegistry(Array.Empty<IAgentTool>());
        var store = new InMemoryConversationStore();
        var llm = new FakeAgentLlmClient();
        var provider = new MutableProjectContextProvider();
        var orchestrator = AgentHarness.Create(llm, registry, store, projectContext: provider);
        var conversationId = Guid.NewGuid();

        await RunTurn(orchestrator, llm, conversationId, "первый");
        await RunTurn(orchestrator, llm, conversationId, "второй");

        Assert.Equal(1, CountSessionContexts(store));

        provider.Context = new AgentProjectContext
        {
            DocumentId = "doc-b",
            Title = "Project B",
            RevitVersion = "2025",
            Units = "mm",
            IsWorkshared = true
        };
        await RunTurn(orchestrator, llm, conversationId, "третий");

        Assert.Equal(2, CountSessionContexts(store));
        var lastRequest = llm.Requests[^1].Messages;
        Assert.Contains(lastRequest, message =>
            (message.Content ?? string.Empty).Contains("document_id: doc-b", StringComparison.Ordinal));
        Assert.Contains(lastRequest, message =>
            (message.Content ?? string.Empty).Contains("workshared: yes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Restart_DoesNotDuplicateThePersistedSessionContext()
    {
        var registry = new AgentToolRegistry(Array.Empty<IAgentTool>());
        var store = new InMemoryConversationStore();
        var provider = new MutableProjectContextProvider();
        var conversationId = Guid.NewGuid();

        var firstLlm = new FakeAgentLlmClient();
        var first = AgentHarness.Create(firstLlm, registry, store, projectContext: provider);
        await RunTurn(first, firstLlm, conversationId, "первый");

        Assert.Equal(1, CountSessionContexts(store));

        // A new orchestrator and session context simulate an app restart.
        var secondLlm = new FakeAgentLlmClient();
        var second = AgentHarness.Create(secondLlm, registry, store, projectContext: provider);
        await RunTurn(second, secondLlm, conversationId, "второй");

        Assert.Equal(1, CountSessionContexts(store));
    }

    [Fact]
    public void TurnContext_CarriesDateModeViewAndCounters()
    {
        var context = Context("Project A", "План 1", 1, 42);
        var state = new WorkingState { Goal = "переименовать виды" };

        var turnContext = AgentSystemPrompt.BuildTurnContext(
            context,
            AgentApprovalMode.ReadOnly,
            state,
            new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc));

        Assert.StartsWith("<turn_context>", turnContext, StringComparison.Ordinal);
        Assert.Contains("date: 2026-11-20", turnContext, StringComparison.Ordinal);
        Assert.Contains("mode: ReadOnly", turnContext, StringComparison.Ordinal);
        Assert.Contains("goal: переименовать виды", turnContext, StringComparison.Ordinal);
        Assert.Contains("current_view: План 1 (id 1)", turnContext, StringComparison.Ordinal);
        Assert.Contains("levels 3/views 42/sheets 2", turnContext, StringComparison.Ordinal);
        Assert.Contains("## Approval mode: ReadOnly", turnContext, StringComparison.Ordinal);
    }

    private static Task RunTurn(
        AgentOrchestrator orchestrator,
        FakeAgentLlmClient llm,
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

    private static int CountSessionContexts(InMemoryConversationStore store) =>
        store.Messages.Count(message =>
            (message.Content ?? string.Empty).StartsWith(
                AgentSystemPrompt.SessionContextHeader, StringComparison.Ordinal));

    private static AgentProjectContext Context(
        string title,
        string viewName,
        long viewId,
        int viewCount = 120) =>
        new()
        {
            DocumentId = "doc-a",
            Title = title,
            RevitVersion = "2025",
            Units = "mm",
            ActiveViewId = viewId,
            ActiveViewName = viewName,
            ActiveViewType = "FloorPlan",
            LevelCount = 3,
            ViewCount = viewCount,
            SheetCount = 2
        };
}