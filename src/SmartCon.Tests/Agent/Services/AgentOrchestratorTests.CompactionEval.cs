using SmartCon.Agent.Services;
using SmartCon.Agent.Services.Llm;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// H1-18 compaction eval: ten cases that must hold for a 200-turn session —
/// facts, IDs, change sets, plan digest, restart, summarizer failure, tool
/// pairs, constraint pinning, protected results and the summarizer input cap.
/// Uses a deterministic summarizer so the gate measures the compactor, not a
/// model's wording.
/// </summary>
public sealed partial class AgentOrchestratorTests
{
    [Fact]
    public async Task CompactionEval_01_TwentyFactsSurvive200Turns()
    {
        var facts = Enumerable.Range(1, 20).Select(i => "FACT-" + i).ToArray();
        var messages = new List<AgentMessage>();
        for (var turn = 0; turn < 200; turn++)
        {
            var fact = turn % 10 == 0 && turn / 10 < 20
                ? " " + facts[turn / 10]
                : string.Empty;
            messages.Add(AgentMessage.User("шаг " + turn + fact + " " + new string('я', 300)));
            messages.Add(AgentMessage.Assistant("ответ " + turn));
        }

        var summarizer = new EvalSummarizer();
        var compactor = new AgentContextCompactor(summarizer);
        var result = await compactor.CompactAsync(
            EvalRequest(messages, budget: 20_000, threshold: 0.5),
            CancellationToken.None);

        Assert.True(result.Summarized);
        var text = string.Join("\n", result.Messages.Select(m => m.Content));
        var survived = facts.Count(fact => text.Contains(fact, StringComparison.Ordinal));
        Assert.True(survived >= 19, "facts preserved: " + survived + "/20");
    }

    [Fact]
    public async Task CompactionEval_02_ElementIdsSurvive()
    {
        var result = await CompactWithFacts(
            "ElementId 12345", "UniqueId abc-def-123");

        Assert.Contains("12345", result.Summary!, StringComparison.Ordinal);
        Assert.Contains("abc-def-123", result.Summary!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompactionEval_03_ChangeSetIdsSurvive()
    {
        var result = await CompactWithFacts("changeSetId cs-777");

        Assert.Contains("cs-777", result.Summary!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompactionEval_04_PlanDigestSurvives()
    {
        var result = await CompactWithFacts("planDigest 0f1e2d3c");

        Assert.Contains("0f1e2d3c", result.Summary!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompactionEval_05_RestartRestoresTheCheckpoint()
    {
        var conversationId = Guid.NewGuid();
        var conversationStore = new InMemoryConversationStore();
        conversationStore.Messages.Add(AgentMessage.User(
            "OLDEST-MARKER " + new string('я', 6000)));
        for (var index = 0; index < 6; index++)
        {
            conversationStore.Messages.Add(AgentMessage.User(
                "шаг " + index + " " + new string('я', 6000)));
            conversationStore.Messages.Add(AgentMessage.Assistant("ответ " + index));
        }

        var compactionStore = new EvalCompactionStore();
        var summarizer = new EvalSummarizer { Result = "CHECKPOINT-STATE" };
        var settings = new AgentLlmSettings
        {
            Model = "test-model",
            ContextWindowTokens = 10_000,
            ContextThreshold = 0.3
        };

        var firstRegistry = new AgentToolRegistry(Array.Empty<IAgentTool>());
        var firstLlm = new FakeAgentLlmClient();
        firstLlm.EnqueueAssistant("ok");
        var first = CreateOrchestrator(
            firstLlm,
            CreateExecutor(firstRegistry),
            firstRegistry,
            conversationStore,
            settings: settings,
            contextCompactor: new AgentContextCompactor(summarizer, store: compactionStore),
            compactionStore: compactionStore);

        await first.RunTurnAsync(
            conversationId,
            "продолжай",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);
        Assert.NotEmpty(compactionStore.Saved);

        var secondRegistry = new AgentToolRegistry(Array.Empty<IAgentTool>());
        var secondLlm = new FakeAgentLlmClient();
        secondLlm.EnqueueAssistant("ok");
        var second = CreateOrchestrator(
            secondLlm,
            CreateExecutor(secondRegistry),
            secondRegistry,
            conversationStore,
            settings: settings,
            contextCompactor: new AgentContextCompactor(summarizer, store: compactionStore),
            compactionStore: compactionStore);

        await second.RunTurnAsync(
            conversationId,
            "ещё",
            Array.Empty<AgentAttachment>(),
            null,
            CancellationToken.None);

        var request = secondLlm.Requests[0].Messages;
        Assert.Contains(
            request,
            message => message.Role == AgentRole.System
                && (message.Content ?? string.Empty).Contains(
                    "<conversation_checkpoint>", StringComparison.Ordinal));
        Assert.DoesNotContain(
            request,
            message => (message.Content ?? string.Empty).Contains(
                "OLDEST-MARKER", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompactionEval_06_SummarizerErrorFallsBack()
    {
        var summarizer = new EvalSummarizer { Failure = new InvalidOperationException("down") };
        var compactor = new AgentContextCompactor(summarizer);

        var result = await compactor.CompactAsync(
            EvalRequest(BuildEvalHistory(), budget: 10_000, threshold: 0.3),
            CancellationToken.None);

        Assert.True(result.Fallback);
        Assert.NotEmpty(result.Messages);
    }

    [Fact]
    public async Task CompactionEval_07_ToolPairsStayIntact()
    {
        var messages = new List<AgentMessage>();
        for (var index = 0; index < 12; index++)
        {
            messages.Add(AgentMessage.User("шаг " + index + " " + new string('я', 2000)));
            messages.Add(AgentMessage.Assistant(
                null,
                new[]
                {
                    new AgentToolCall
                    {
                        Id = "call_" + index,
                        Name = "list_views",
                        ArgumentsJson = "{}"
                    }
                }));
            messages.Add(AgentMessage.ToolResult(
                "call_" + index, "{\"success\":true,\"data\":{\"items\":\"" +
                new string('x', 2000) + "\"}}"));
        }

        var compactor = new AgentContextCompactor(new EvalSummarizer());
        var result = await compactor.CompactAsync(
            EvalRequest(messages, budget: 12_000, threshold: 0.3, keepRecentMessages: 2),
            CancellationToken.None);

        var resultText = string.Join("\n", result.Messages.Select(m => m.Content));
        Assert.Contains("CHECKPOINT", resultText, StringComparison.Ordinal);
        for (var index = 0; index < result.Messages.Count; index++)
        {
            if (result.Messages[index].Role != AgentRole.Tool)
                continue;

            var id = result.Messages[index].ToolCallId;
            Assert.Contains(
                result.Messages.Take(index),
                message => message.ToolCalls.Any(call => call.Id == id));
        }
    }

    [Fact]
    public async Task CompactionEval_08_UserConstraintsArePinned()
    {
        var state = new WorkingState { Goal = "настроить виды" };
        state.Constraints.Add("не трогать листы");
        state.Constraints.Add("мм, не футы");
        var compactor = new AgentContextCompactor(new EvalSummarizer());
        var result = await compactor.CompactAsync(
            new AgentCompactionRequest
            {
                Messages = BuildEvalHistory(),
                ContextBudgetTokens = 10_000,
                Threshold = 0.3,
                KeepRecentMessages = 2,
                WorkingState = state,
                SessionId = Guid.NewGuid()
            },
            CancellationToken.None);

        Assert.Contains("не трогать листы", result.Summary!, StringComparison.Ordinal);
        Assert.Contains("мм, не футы", result.Summary!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompactionEval_09_ProtectedToolResultsSurvive()
    {
        var protectedPayload =
            "{\"success\":true,\"data\":{\"items\":\"" + new string('p', 2000) + "\"}}";
        var messages = new List<AgentMessage>
        {
            AgentMessage.Assistant(
                null,
                new[]
                {
                    new AgentToolCall
                    {
                        Id = "read",
                        Name = "read_tool_result",
                        ArgumentsJson = "{}"
                    }
                }),
            AgentMessage.ToolResult("read", protectedPayload)
        };
        for (var index = 0; index < 2; index++)
        {
            messages.Add(AgentMessage.User("шаг " + index + " " + new string('я', 1000)));
            messages.Add(AgentMessage.Assistant("ответ " + index));
        }

        for (var index = 0; index < 4; index++)
        {
            messages.Add(AgentMessage.Assistant(
                null,
                new[]
                {
                    new AgentToolCall
                    {
                        Id = "call_" + index,
                        Name = "list_views",
                        ArgumentsJson = "{}"
                    }
                }));
            messages.Add(AgentMessage.ToolResult(
                "call_" + index,
                "{\"success\":true,\"data\":{\"items\":\"" + new string('x', 3000) + "\"}}"));
        }

        var compactor = new AgentContextCompactor(new EvalSummarizer());
        var result = await compactor.CompactAsync(
            EvalRequest(
                messages, budget: 10_000, threshold: 0.3,
                keepRecentMessages: 20, keepRecentToolResults: 0),
            CancellationToken.None);

        Assert.False(result.Summarized);
        Assert.Equal(4, result.MaskedCount);
        Assert.Contains(
            result.Messages,
            message => message.ToolCallId == "read"
                && (message.Content ?? string.Empty).Contains('p', StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompactionEval_10_SummaryInputRespectsTheCap()
    {
        var settings = new AgentLlmSettings { SummaryMaxInputTokens = 4_000 };
        var summarizer = new EvalSummarizer();
        var compactor = new AgentContextCompactor(summarizer, settings);
        var messages = new List<AgentMessage>();
        for (var index = 0; index < 20; index++)
            messages.Add(AgentMessage.User("шаг " + index + " " + new string('я', 3000)));
        messages.Add(AgentMessage.User("дальше"));

        var result = await compactor.CompactAsync(
            EvalRequest(messages, budget: 10_000, threshold: 0.3, keepRecentMessages: 1),
            CancellationToken.None);

        Assert.True(result.Summarized);
        Assert.All(
            summarizer.Inputs,
            input => Assert.True(
                AgentTokenEstimator.EstimateText(input) <= 4_000,
                "input tokens=" + AgentTokenEstimator.EstimateText(input)));
    }

    private static async Task<AgentCompactionResult> CompactWithFacts(params string[] facts)
    {
        var messages = new List<AgentMessage>();
        for (var index = 0; index < 4; index++)
        {
            messages.Add(AgentMessage.User(
                "данные: " + string.Join(", ", facts) + " " + new string('я', 4000)));
            messages.Add(AgentMessage.Assistant("принято"));
        }

        var compactor = new AgentContextCompactor(new EvalSummarizer());
        return await compactor.CompactAsync(
            EvalRequest(messages, budget: 10_000, threshold: 0.3, keepRecentMessages: 1),
            CancellationToken.None);
    }

    private static List<AgentMessage> BuildEvalHistory()
    {
        var messages = new List<AgentMessage>();
        for (var index = 0; index < 8; index++)
        {
            messages.Add(AgentMessage.User(
                "CHECKPOINT-FACT " + index + " " + new string('я', 4000)));
            messages.Add(AgentMessage.Assistant("ответ " + index));
        }

        return messages;
    }

    private static AgentCompactionRequest EvalRequest(
        IReadOnlyList<AgentMessage> messages,
        int budget,
        double threshold,
        int keepRecentMessages = 8,
        int keepRecentToolResults = 6) =>
        new()
        {
            Messages = messages,
            ContextBudgetTokens = budget,
            Threshold = threshold,
            KeepRecentMessages = keepRecentMessages,
            KeepRecentToolResults = keepRecentToolResults,
            HardShrinkMessages = 2,
            SessionId = Guid.NewGuid()
        };

    private sealed class EvalSummarizer : IAgentSummarizer
    {
        public List<string> Inputs { get; } = new();

        public string Result { get; set; } = "CHECKPOINT";

        public Exception? Failure { get; set; }

        public Task<string> SummarizeAsync(
            string input,
            int maxOutputTokens,
            CancellationToken ct)
        {
            Inputs.Add(input);
            if (Failure is not null)
                throw Failure;

            // Deterministic extractor: keeps every fact-looking token so the gate
            // measures whether the compactor fed the facts to the summarizer.
            var facts = input
                .Split('\n')
                .SelectMany(line => line.Split(' '))
                .Where(token => token.Length >= 4
                    && (token.StartsWith("FACT", StringComparison.Ordinal)
                        || token.StartsWith("Element", StringComparison.Ordinal)
                        || token.StartsWith("Unique", StringComparison.Ordinal)
                        || token.StartsWith("change", StringComparison.Ordinal)
                        || token.StartsWith("plan", StringComparison.Ordinal)
                        || token.Contains("12345", StringComparison.Ordinal)
                        || token.Contains("abc-def", StringComparison.Ordinal)
                        || token.Contains("cs-777", StringComparison.Ordinal)
                        || token.Contains("0f1e2d3c", StringComparison.Ordinal)
                        || token.Contains("CHECKPOINT-FACT", StringComparison.Ordinal)))
                .Distinct(StringComparer.Ordinal);

            var extracted = string.Join(" ", facts);
            return Task.FromResult(
                extracted.Length == 0 ? Result : Result + " " + extracted);
        }
    }

    private sealed class EvalCompactionStore : IAgentCompactionStore
    {
        public List<AgentCompactionRecord> Saved { get; } = new();

        public Task SaveAsync(AgentCompactionRecord record, CancellationToken ct)
        {
            Saved.Add(record);
            return Task.CompletedTask;
        }

        public Task<AgentCompactionRecord?> GetLatestAsync(Guid sessionId, CancellationToken ct) =>
            Task.FromResult(Saved.Count == 0 ? null : Saved[^1]);
    }
}
