using SmartCon.Agent.Services;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

public sealed class AgentContextCompactorTests
{
    private const int Budget = 10_000;
    private const double Threshold = 0.3;

    private static readonly Guid Session = Guid.Parse(
        "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public async Task BelowThreshold_DoesNothing()
    {
        var summarizer = new FakeSummarizer();
        var compactor = new AgentContextCompactor(summarizer);
        var messages = new List<AgentMessage> { AgentMessage.User("привет") };

        var result = await compactor.CompactAsync(Request(messages), CancellationToken.None);

        Assert.False(result.Changed);
        var message = Assert.Single(result.Messages);
        Assert.Equal("привет", message.Content);
        Assert.Empty(summarizer.Inputs);
    }

    [Fact]
    public async Task OverBudget_MasksOldToolResultsOnly()
    {
        var summarizer = new FakeSummarizer();
        var compactor = new AgentContextCompactor(summarizer);
        var messages = new List<AgentMessage>();
        var large = LargeToolPayload(12_000);
        var small = LargeToolPayload(50);

        for (var index = 0; index < 10; index++)
        {
            messages.Add(AssistantCall("call_" + index, "get_test"));
            messages.Add(AgentMessage.ToolResult(
                "call_" + index, index < 4 ? large : small));
        }

        var result = await compactor.CompactAsync(
            Request(messages, keepRecentToolResults: 6), CancellationToken.None);

        Assert.True(result.Changed);
        Assert.False(result.Summarized);
        Assert.Empty(summarizer.Inputs);
        Assert.Equal(4, result.MaskedCount);
        var toolMessages = result.Messages.Where(m => m.Role == AgentRole.Tool).ToList();
        Assert.Equal(10, toolMessages.Count);
        Assert.Equal(4, toolMessages.Count(m =>
            m.Content!.Contains("\"compacted\":true", StringComparison.Ordinal)));
        Assert.Equal(6, toolMessages.Count(m =>
            !m.Content!.Contains("\"compacted\":true", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task MaskedDigest_KeepsTheResultHandle()
    {
        var summarizer = new FakeSummarizer();
        var compactor = new AgentContextCompactor(summarizer);
        var messages = new List<AgentMessage>();
        for (var index = 0; index < 4; index++)
        {
            messages.Add(AssistantCall("call_" + index, "get_test"));
            messages.Add(AgentMessage.ToolResult(
                "call_" + index,
                "{\"success\":true,\"affectedCount\":3,\"data\":{\"truncated\":true," +
                "\"resultHandle\":\"call_" + index + "\",\"items\":\"" +
                new string('x', 9000) + "\"}}"));
        }

        var result = await compactor.CompactAsync(
            Request(messages, keepRecentToolResults: 0), CancellationToken.None);

        Assert.Equal(4, result.MaskedCount);
        Assert.Contains("\"resultHandle\":\"call_0\"", result.Messages[1].Content, StringComparison.Ordinal);
        Assert.Contains("read_tool_result", result.Messages[1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProtectedTools_AreNeverMasked()
    {
        var summarizer = new FakeSummarizer();
        var compactor = new AgentContextCompactor(summarizer);
        var large = LargeToolPayload(3000);
        var messages = new List<AgentMessage>
        {
            AssistantCall("plan", "execute_plan"),
            AgentMessage.ToolResult("plan", large),
            AssistantCall("read", "read_tool_result"),
            AgentMessage.ToolResult("read", large)
        };
        for (var index = 0; index < 4; index++)
        {
            messages.Add(AssistantCall("call_" + index, "get_test"));
            messages.Add(AgentMessage.ToolResult("call_" + index, large));
        }

        var result = await compactor.CompactAsync(
            Request(messages, keepRecentToolResults: 0), CancellationToken.None);

        var toolMessages = result.Messages.Where(m => m.Role == AgentRole.Tool).ToList();
        Assert.Contains(toolMessages, m => m.ToolCallId == "plan"
            && m.Content!.Contains("original", StringComparison.Ordinal));
        Assert.Contains(toolMessages, m => m.ToolCallId == "read"
            && m.Content!.Contains("original", StringComparison.Ordinal));
        Assert.Equal(4, result.MaskedCount);
    }

    [Fact]
    public async Task OverBudget_SummarizesAndPinsConstraints()
    {
        var summarizer = new FakeSummarizer { Result = "1. Goal\n2. Constraints" };
        var compactor = new AgentContextCompactor(summarizer);
        var messages = new List<AgentMessage>();
        for (var index = 0; index < 8; index++)
        {
            messages.Add(AgentMessage.User(
                "FACT: fact-" + index + " " + new string('я', 6000)));
            messages.Add(AgentMessage.Assistant("шаг " + index));
        }

        var state = new WorkingState { Goal = "переименовать виды" };
        state.Constraints.Add("не трогать листы");

        var result = await compactor.CompactAsync(
            Request(messages, keepRecentMessages: 2, workingState: state),
            CancellationToken.None);

        Assert.True(result.Summarized);
        Assert.NotNull(result.Summary);
        Assert.Contains("не трогать листы", result.Summary!, StringComparison.Ordinal);
        Assert.Contains("переименовать виды", result.Summary!, StringComparison.Ordinal);
        Assert.Equal(AgentRole.System, result.Messages[0].Role);
        Assert.Contains("<conversation_checkpoint>", result.Messages[0].Content, StringComparison.Ordinal);
        Assert.True(result.Messages.Count <= 3);
        Assert.All(
            summarizer.Inputs,
            input => Assert.Contains("FACT: fact-", input, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckpointTail_DoesNotStartWithAnOrphanToolResult()
    {
        var summarizer = new FakeSummarizer { Result = "summary" };
        var compactor = new AgentContextCompactor(summarizer);
        var messages = new List<AgentMessage>();
        for (var index = 0; index < 6; index++)
        {
            messages.Add(AgentMessage.User(new string('я', 5000)));
            messages.Add(AssistantCall("call_" + index, "get_test"));
            messages.Add(AgentMessage.ToolResult("call_" + index, LargeToolPayload(100)));
        }

        var result = await compactor.CompactAsync(
            Request(messages, keepRecentMessages: 1, keepRecentToolResults: 0),
            CancellationToken.None);

        Assert.True(result.Summarized);
        Assert.NotEqual(AgentRole.Tool, result.Messages[1].Role);
    }

    [Fact]
    public async Task SummarizerFailure_FallsBackAndOpensTheCircuitBreaker()
    {
        var summarizer = new FakeSummarizer { Failure = new InvalidOperationException("boom") };
        var compactor = new AgentContextCompactor(summarizer);
        var messages = BuildLargeHistory();

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var result = await compactor.CompactAsync(
                Request(messages, keepRecentMessages: 2), CancellationToken.None);
            Assert.True(result.Fallback);
            Assert.False(result.Summarized);
            Assert.NotEmpty(result.Messages);
        }

        Assert.Equal(3, summarizer.Inputs.Count);
    }

    [Fact]
    public async Task LongSummaryInput_IsChunkedUnderTheCap()
    {
        var summarizer = new FakeSummarizer { Result = "part" };
        var settings = new SmartCon.Agent.Services.Llm.AgentLlmSettings
        {
            SummaryMaxInputTokens = 4_000
        };
        var compactor = new AgentContextCompactor(summarizer, settings);
        var messages = new List<AgentMessage>();
        for (var index = 0; index < 20; index++)
            messages.Add(AgentMessage.User("шаг " + index + " " + new string('я', 3000)));
        messages.Add(AgentMessage.User("дальше"));

        var result = await compactor.CompactAsync(
            Request(messages, keepRecentMessages: 1), CancellationToken.None);

        Assert.True(result.Summarized);
        Assert.True(summarizer.Inputs.Count > 1);
        Assert.All(
            summarizer.Inputs,
            input => Assert.True(
                AgentTokenEstimator.EstimateText(input) <= 4_000,
                "chunk tokens=" + AgentTokenEstimator.EstimateText(input)));
    }

    [Fact]
    public async Task Checkpoint_IsPersistedWithTheCoveredCount()
    {
        var summarizer = new FakeSummarizer { Result = "summary" };
        var store = new InMemoryCompactionStore();
        var compactor = new AgentContextCompactor(summarizer, store: store);

        var result = await compactor.CompactAsync(
            Request(BuildLargeHistory(), keepRecentMessages: 2, covered: 42),
            CancellationToken.None);

        Assert.True(result.Summarized);
        var saved = Assert.Single(store.Saved);
        Assert.Equal(42, saved.ToRowId);
        Assert.Equal(Session, saved.SessionId);
        Assert.Equal(result.Summary, saved.Summary);
    }

    [Fact]
    public async Task SummaryPath_SkipsSummarizerAfterThreeFailures()
    {
        var summarizer = new FakeSummarizer { Failure = new Exception("down") };
        var compactor = new AgentContextCompactor(summarizer);
        var messages = BuildLargeHistory();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await compactor.CompactAsync(
                Request(messages, keepRecentMessages: 2), CancellationToken.None);
        }

        var fourth = await compactor.CompactAsync(
            Request(messages, keepRecentMessages: 2), CancellationToken.None);

        Assert.True(fourth.Fallback);
        Assert.Equal(3, summarizer.Inputs.Count);
    }

    private static AgentCompactionRequest Request(
        IReadOnlyList<AgentMessage> messages,
        int keepRecentToolResults = 6,
        int keepRecentMessages = 8,
        WorkingState? workingState = null,
        int covered = 0) =>
        new()
        {
            Messages = messages,
            ContextBudgetTokens = Budget,
            Threshold = Threshold,
            KeepRecentToolResults = keepRecentToolResults,
            KeepRecentMessages = keepRecentMessages,
            HardShrinkMessages = 2,
            CoveredMessageCount = covered,
            SessionId = Session,
            WorkingState = workingState
        };

    private static List<AgentMessage> BuildLargeHistory()
    {
        var messages = new List<AgentMessage>();
        for (var index = 0; index < 8; index++)
        {
            messages.Add(AgentMessage.User("FACT: fact-" + index + " " + new string('я', 6000)));
            messages.Add(AgentMessage.Assistant("шаг " + index));
        }

        return messages;
    }

    private static AgentMessage AssistantCall(string id, string name) =>
        AgentMessage.Assistant(
            null,
            new[] { new AgentToolCall { Id = id, Name = name, ArgumentsJson = "{}" } });

    private static string LargeToolPayload(int padding) =>
        "{\"success\":true,\"message\":\"original\",\"affectedCount\":2,\"data\":{\"items\":\"" +
        new string('x', padding) + "\"}}";

    internal sealed class FakeSummarizer : IAgentSummarizer
    {
        public List<string> Inputs { get; } = new();

        public string Result { get; set; } = "summary";

        public Exception? Failure { get; set; }

        public Task<string> SummarizeAsync(
            string input,
            int maxOutputTokens,
            CancellationToken ct)
        {
            Inputs.Add(input);
            if (Failure is not null)
                throw Failure;

            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Result);
        }
    }

    internal sealed class InMemoryCompactionStore : IAgentCompactionStore
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
