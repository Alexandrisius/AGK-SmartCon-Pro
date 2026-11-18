using System.Text.Json;
using SmartCon.Agent.Services;
using SmartCon.Agent.Services.Llm;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Eval;

/// <summary>
/// A4: offline orchestrator eval. Runs the production <see cref="AgentOrchestrator"/>
/// against <see cref="FakeAgentLlmClient"/> and scripted tools, so the tool-call
/// loop, the round limit, history compaction and tool-call pairing are covered in
/// CI without Revit, network or API keys. Revit-bound eval tasks stay in
/// SmartCon.IntegrationTests.
/// </summary>
public sealed class AgentOfflineOrchestratorEvalTests
{
    [Fact]
    public async Task MultiRound_ToolLoop_PairsEveryCall()
    {
        var listViews = new ScriptedTool("list_views", AgentToolRisk.Read, supportsDryRun: false);
        var settings = new ScriptedTool("get_view_settings", AgentToolRisk.Read, supportsDryRun: false);
        var registry = new AgentToolRegistry(new IAgentTool[] { listViews, settings });
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant(
            null,
            new[]
            {
                new AgentToolCall { Id = "call_1", Name = "list_views", ArgumentsJson = "{}" },
                new AgentToolCall { Id = "call_2", Name = "get_view_settings", ArgumentsJson = "{\"viewId\":1}" }
            });
        llm.EnqueueAssistant("Готово");

        var orchestrator = CreateOrchestrator(llm, registry);

        var result = await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "покажи виды и настройки",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        Assert.Equal("Готово", result.FinalText);
        Assert.Equal(2, result.ToolCallCount);
        Assert.Equal(2, result.Rounds);
        Assert.Single(listViews.Executions);
        Assert.Single(settings.Executions);

        var secondRequest = llm.Requests[1].Messages;
        var assistantCalls = secondRequest
            .Where(message => message.Role == AgentRole.Assistant)
            .SelectMany(message => message.ToolCalls)
            .Select(call => call.Id)
            .ToList();
        var toolResults = secondRequest
            .Where(message => message.Role == AgentRole.Tool)
            .ToList();

        Assert.Equal(2, assistantCalls.Count);
        Assert.Equal(2, toolResults.Count);
        Assert.All(toolResults, message => Assert.Contains(message.ToolCallId, assistantCalls));
        Assert.All(assistantCalls, id => Assert.Contains(toolResults, message => message.ToolCallId == id));
    }

    [Fact]
    public async Task RoundLimit_StopsAtEightRounds_AndReportsIt()
    {
        var tool = new ScriptedTool("list_views", AgentToolRisk.Read, supportsDryRun: false);
        var registry = new AgentToolRegistry(new IAgentTool[] { tool });
        var llm = new FakeAgentLlmClient();
        for (var round = 1; round <= 8; round++)
        {
            llm.EnqueueAssistant(
                null,
                new[]
                {
                    new AgentToolCall
                    {
                        Id = "call_" + round,
                        Name = "list_views",
                        ArgumentsJson = "{\"viewId\":" + round + "}"
                    }
                });
        }

        var orchestrator = CreateOrchestrator(llm, registry);

        var result = await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "цикл",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        Assert.True(result.MaxRoundsReached);
        Assert.Equal(8, result.Rounds);
        Assert.Equal(8, result.ToolCallCount);
        Assert.Equal(8, tool.Executions.Count);
        Assert.Equal(8, llm.Requests.Count);
        Assert.Contains("8", result.FinalText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compaction_OverBudget_ReplacesOldToolResults_AndKeepsPairing()
    {
        var store = new EvalConversationStore();
        var conversationId = Guid.NewGuid();
        for (var index = 0; index < 10; index++)
        {
            var callId = "call_" + index;
            store.Messages.Add(AgentMessage.Assistant(
                null,
                new[]
                {
                    new AgentToolCall { Id = callId, Name = "list_views", ArgumentsJson = "{}" }
                }));
            store.Messages.Add(AgentMessage.ToolResult(callId, BigToolResult()));
        }

        store.Messages.Add(AgentMessage.User("продолжай"));

        var registry = new AgentToolRegistry(Array.Empty<IAgentTool>());
        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant("Ок");
        var orchestrator = CreateOrchestrator(
            llm,
            registry,
            store: store,
            settings: new AgentLlmSettings
            {
                Model = "test-model",
                ContextWindowTokens = 2000,
                ContextThreshold = 0.3,
                ToolCatalogV2 = false,
                ContextCompactionV2 = false,
                WorkingStateEnabled = false
            });

        var result = await orchestrator.RunTurnAsync(
            conversationId,
            "продолжай",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        Assert.Equal("Ок", result.FinalText);
        var sent = llm.Requests.Single().Messages;
        var toolMessages = sent.Where(message => message.Role == AgentRole.Tool).ToList();
        Assert.Equal(10, toolMessages.Count);
        Assert.Equal(
            6,
            toolMessages.Count(message => message.Content!.Contains("\"compacted\":true", StringComparison.Ordinal)));
        Assert.All(
            toolMessages.Take(6),
            message => Assert.Contains("\"compacted\":true", message.Content, StringComparison.Ordinal));
        Assert.All(
            toolMessages.Skip(6),
            message => Assert.DoesNotContain("\"compacted\":true", message.Content, StringComparison.Ordinal));
        Assert.All(toolMessages, message => Assert.NotNull(message.ToolCallId));
    }

    [Fact]
    public async Task FindTools_ExpandsTheCatalogForTheNextRound()
    {
        var findTools = new ScriptedTool(
            "find_tools",
            AgentToolRisk.Read,
            supportsDryRun: false,
            handler: _ => ToolResult.Ok(
                JsonSerializer.SerializeToElement(new
                {
                    tools = new[] { new { name = "rename_views" } }
                }),
                "found 1"));
        var rename = new ScriptedTool("rename_views", AgentToolRisk.Write);
        var deleteViews = new ScriptedTool("delete_views", AgentToolRisk.Destructive);
        var setParams = new ScriptedTool("set_parameters", AgentToolRisk.Write);
        var registry = new AgentToolRegistry(
            new IAgentTool[] { findTools, rename, deleteViews, setParams });
        var router = new RecordingToolRouter
        {
            Result = new ToolRoutingDecision
            {
                Tools = new[] { findTools.Definition }
            }
        };

        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant(
            null,
            new[]
            {
                new AgentToolCall
                {
                    Id = "call_1",
                    Name = "find_tools",
                    ArgumentsJson = "{\"query\":\"переименовать виды\"}"
                }
            });
        llm.EnqueueAssistant("Нашёл rename_views");

        var orchestrator = CreateOrchestrator(
            llm,
            registry,
            settings: new AgentLlmSettings
            {
                Model = "test-model",
                UseToolRouter = true,
                ToolCatalogV2 = false,
                ContextCompactionV2 = false,
                WorkingStateEnabled = false
            },
            toolRouter: router);

        var result = await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "переименуй виды",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        Assert.Equal("Нашёл rename_views", result.FinalText);
        Assert.Equal("переименуй виды", router.LastQuery);
        Assert.Single(llm.Requests[0].Tools);
        Assert.Equal(4, llm.Requests[1].Tools.Count);
        Assert.Contains(llm.Requests[1].Tools, tool => tool.Name == "rename_views");
    }

    [Fact]
    public async Task ExecutePlan_DryRunThenApply_WithDigest()
    {
        var rename = new ScriptedTool("rename_views", AgentToolRisk.Write);
        var registry = new AgentToolRegistry(new IAgentTool[] { rename });
        var session = new AgentSessionContext
        {
            DocumentId = "doc",
            ApprovalMode = AgentApprovalMode.AutoApproveSession
        };
        var planTool = new ExecutePlanTool(() => registry, new ImmediateAwaitableEvent(), null, session);
        var outerRegistry = new AgentToolRegistry(new IAgentTool[] { rename, planTool });

        var plan = new AgentPlan
        {
            Goal = "переименовать вид",
            Steps = new[]
            {
                new AgentPlanStep
                {
                    StepId = "s1",
                    ToolName = "rename_views",
                    ArgumentsJson = "{\"viewId\":1}",
                    ExpectedChanges = 1
                }
            }
        };
        var digest = AgentPlanExecutor.ComputeDigest(plan, "doc");

        var llm = new FakeAgentLlmClient();
        llm.EnqueueAssistant(
            null,
            new[]
            {
                new AgentToolCall
                {
                    Id = "preview",
                    Name = "execute_plan",
                    ArgumentsJson =
                        "{\"goal\":\"переименовать вид\",\"dryRun\":true,\"steps\":[" +
                        "{\"stepId\":\"s1\",\"toolName\":\"rename_views\"," +
                        "\"arguments\":{\"viewId\":1},\"expectedChanges\":1}]}"
                }
            });
        llm.EnqueueAssistant(
            null,
            new[]
            {
                new AgentToolCall
                {
                    Id = "apply",
                    Name = "execute_plan",
                    ArgumentsJson =
                        "{\"goal\":\"переименовать вид\",\"dryRun\":false,\"planDigest\":\"" +
                        digest + "\",\"steps\":[" +
                        "{\"stepId\":\"s1\",\"toolName\":\"rename_views\"," +
                        "\"arguments\":{\"viewId\":1},\"expectedChanges\":1}]}"
                }
            });
        llm.EnqueueAssistant("Готово");

        var orchestrator = CreateOrchestrator(llm, outerRegistry, session: session);

        var result = await orchestrator.RunTurnAsync(
            Guid.NewGuid(),
            "переименуй вид",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        Assert.Equal("Готово", result.FinalText);
        Assert.Equal(2, result.ToolCallCount);
        Assert.Equal(3, result.Rounds);
        // Preview dry run, apply-time fidelity dry run, then the approved apply.
        Assert.Equal(3, rename.Executions.Count);
        // Plan approval covers every step, so all three runs are approved for the
        // tool; the first two still carry dryRun=true and never write.
        Assert.All(rename.Executions, execution => Assert.True(execution.Approved));
        Assert.Equal(
            "true|true|false",
            string.Join("|", rename.Executions.Select(execution =>
                execution.ArgumentsJson.Contains("\"dryRun\":false", StringComparison.Ordinal)
                    ? "false"
                    : "true")));

        var toolPayloads = new List<string>();
        for (var round = 0; round < llm.Requests.Count; round++)
        {
            toolPayloads.AddRange(llm.Requests[round].Messages
                .Where(message => message.Role == AgentRole.Tool)
                .Select(message => message.Content ?? string.Empty));
        }

        Assert.Contains(toolPayloads, payload =>
            payload.Contains("\"dryRun\":true", StringComparison.Ordinal)
            && payload.Contains(digest, StringComparison.Ordinal));
        Assert.Contains(toolPayloads, payload =>
            payload.Contains("\"dryRun\":false", StringComparison.Ordinal)
            && payload.Contains("\"verified\"", StringComparison.Ordinal));
    }

    private static AgentOrchestrator CreateOrchestrator(
        FakeAgentLlmClient llm,
        IToolRegistry registry,
        AgentSessionContext? session = null,
        AgentLlmSettings? settings = null,
        EvalConversationStore? store = null,
        IToolRouter? toolRouter = null)
    {
        session ??= new AgentSessionContext { DocumentId = "doc" };
        store ??= new EvalConversationStore();
        var executor = new AgentToolExecutor(
            registry,
            new ImmediateAwaitableEvent(),
            session,
            new RecordingConfirmation(),
            new RecordingAuditLog());

        return new AgentOrchestrator(
            llm,
            executor,
            registry,
            store,
            new RecordingAuditLog(),
            new EvalProjectContextProvider(session.DocumentId),
            settings ?? new AgentLlmSettings
            {
                Model = "test-model",
                ToolCatalogV2 = false,
                ContextCompactionV2 = false,
                WorkingStateEnabled = false
            },
            session,
            new AgentPromptBuilder(),
            null,
            null,
            toolRouter);
    }

    private static string BigToolResult()
    {
        var payload = new string('x', 12_000);
        return JsonSerializer.Serialize(new
        {
            success = true,
            message = "ok",
            data = new { items = payload }
        });
    }

    private sealed class EvalConversationStore : IAgentConversationStore
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

    private sealed class EvalProjectContextProvider : IAgentProjectContextProvider
    {
        private readonly string _documentId;

        public EvalProjectContextProvider(string documentId)
        {
            _documentId = documentId;
        }

        public Task<AgentProjectContext> GetAsync(CancellationToken ct) => Task.FromResult(
            new AgentProjectContext
            {
                DocumentId = _documentId,
                Title = "Eval document",
                RevitVersion = "2025"
            });
    }
}
