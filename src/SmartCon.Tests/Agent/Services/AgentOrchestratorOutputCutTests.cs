using SmartCon.Agent.Services;
using SmartCon.Agent.Services.Llm;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

/// <summary>
/// Output cut-off recovery: a completion stopped by the output token limit is
/// discarded (not persisted, its tool calls never executed) and the round is
/// re-requested with the ephemeral reminder, up to two retries.
/// </summary>
public sealed class AgentOrchestratorOutputCutTests
{
    private static AgentLlmSettings Settings { get; } = new()
    {
        Model = "test-model",
        MaxOutputTokens = 5_000
    };

    private static AgentChatResponse TruncatedWithToolCall(int promptTokens = 10) => new()
    {
        Message = AgentMessage.Assistant(
            null,
            new[]
            {
                new AgentToolCall
                {
                    Id = "call_cut",
                    Name = "rename_levels",
                    ArgumentsJson = "{\"items\":[]}"
                }
            }),
        FinishReason = "length",
        Truncated = true,
        Usage = new AgentUsage { PromptTokens = promptTokens, CompletionTokens = 7, TotalTokens = 17 },
        Model = "fake-model",
        Provider = "fake"
    };

    [Fact]
    public async Task TruncatedCompletion_IsDiscardedAndRetriedWithReminder()
    {
        var llm = new FakeAgentLlmClient();
        llm.Enqueue(TruncatedWithToolCall(promptTokens: 10));
        llm.EnqueueAssistant(
            "done",
            usage: new AgentUsage { PromptTokens = 20, CompletionTokens = 3, TotalTokens = 23 });

        var tool = new ScriptedTool("rename_levels");
        var store = new InMemoryConversationStore();
        var usageStore = new CapturingUsageStore();
        var orchestrator = AgentHarness.Create(
            llm, new FakeToolRegistry(tool), store, usageStore: usageStore, settings: Settings);

        var progress = new RecordingProgress();
        var result = await orchestrator.RunTurnAsync(
            Guid.NewGuid(), "переименуй уровни", Array.Empty<AgentAttachment>(),
            progress, CancellationToken.None);

        // Two requests: the discarded attempt and the successful retry.
        Assert.Equal(2, llm.Requests.Count);

        // The retry keeps the normal budget (only the second retry doubles).
        Assert.Equal(5_000, llm.Requests[0].MaxOutputTokens);
        Assert.Equal(5_000, llm.Requests[1].MaxOutputTokens);

        // The retry status message carries the cut marker and the reminder.
        var retryMessages = llm.Requests[1].Messages;
        var reminder = retryMessages[retryMessages.Count - 1].Content ?? string.Empty;
        Assert.Contains("output_cut=\"1\"", reminder, StringComparison.Ordinal);
        Assert.Contains("cut off by the output token limit", reminder, StringComparison.Ordinal);

        // The truncated tool call was never executed.
        Assert.Empty(tool.Executions);

        // Nothing of the truncated attempt was persisted: exactly one assistant
        // message (the final text) and no tool messages at all.
        Assert.Single(store.Messages, message => message.Role == AgentRole.Assistant);
        Assert.DoesNotContain(store.Messages, message => message.Role == AgentRole.Tool);

        Assert.Equal("done", result.FinalText);
        // Usage of the discarded attempt still counts toward the turn total.
        Assert.Equal(30, result.Usage.PromptTokens);

        // The panel is told to close the "running" tool card of the discarded
        // call and shows the retry status line.
        Assert.Contains(progress.Events, e =>
            e.Kind == AgentProgressKind.ToolCompleted
            && e.ToolCallId == "call_cut"
            && e.Success == false
            && e.Error == "output_limit");
        Assert.Contains(progress.Events, e =>
            e.Kind == AgentProgressKind.Status && !string.IsNullOrEmpty(e.Text));
    }

    [Fact]
    public async Task ThreeTruncationsInARow_ThrowAndDoubleTheBudgetOnSecondRetry()
    {
        var llm = new FakeAgentLlmClient();
        for (var i = 0; i < 3; i++)
            llm.Enqueue(TruncatedWithToolCall());

        var tool = new ScriptedTool("rename_levels");
        var store = new InMemoryConversationStore();
        var orchestrator = AgentHarness.Create(
            llm, new FakeToolRegistry(tool), store, settings: Settings);

        var ex = await Assert.ThrowsAnyAsync<AgentLlmException>(() => orchestrator.RunTurnAsync(
            Guid.NewGuid(), "переименуй уровни", Array.Empty<AgentAttachment>(),
            null, CancellationToken.None));

        Assert.Contains("cut off by the output token limit", ex.Message, StringComparison.Ordinal);
        Assert.Contains(
            "Raise Max output tokens in the agent settings",
            ex.Message,
            StringComparison.Ordinal);

        // Attempt 1 normal, retry 1 normal, retry 2 doubled.
        Assert.Equal(3, llm.Requests.Count);
        Assert.Equal(5_000, llm.Requests[0].MaxOutputTokens);
        Assert.Equal(5_000, llm.Requests[1].MaxOutputTokens);
        Assert.Equal(10_000, llm.Requests[2].MaxOutputTokens);

        Assert.Empty(tool.Executions);
        Assert.DoesNotContain(store.Messages, message => message.Role == AgentRole.Assistant);
        Assert.DoesNotContain(store.Messages, message => message.Role == AgentRole.Tool);
    }

    private sealed class RecordingProgress : IProgress<AgentProgress>
    {
        public List<AgentProgress> Events { get; } = new();

        public void Report(AgentProgress value) => Events.Add(value);
    }
}
