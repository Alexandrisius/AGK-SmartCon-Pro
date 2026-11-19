using SmartCon.Agent.Services;
using SmartCon.Core.Models.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

public sealed partial class AgentContextCompactorTests
{
    [Fact]
    public async Task ProjectAsync_MasksOldResultsAndKeepsTheTail()
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

        var result = await compactor.ProjectAsync(
            Request(messages, keepRecentToolResults: 6, minMaskSavings: 1),
            CancellationToken.None);

        Assert.True(result.Changed);
        Assert.False(result.Summarized);
        Assert.Equal(4, result.MaskedCount);
        Assert.True(result.TokensBefore > result.TokensAfter);
        Assert.Equal(
            4,
            result.Messages.Count(m =>
                m.Content?.Contains("\"compacted\":true", StringComparison.Ordinal) == true));
    }

    [Fact]
    public async Task ProjectAsync_BelowPressure_DoesNotShrinkTheWorkingSet()
    {
        // Budget 10 000 × threshold 0.3 = a 3 000-token limit; the projection gate
        // sits at 75 % of it. A history around 1 700 tokens must stay verbatim:
        // masking it only makes the context indicator drop for no reason.
        var summarizer = new FakeSummarizer();
        var compactor = new AgentContextCompactor(summarizer);
        var messages = new List<AgentMessage>
        {
            AssistantCall("call_0", "get_test"),
            AgentMessage.ToolResult("call_0", LargeToolPayload(3_000)),
            AssistantCall("call_1", "get_test"),
            AgentMessage.ToolResult("call_1", LargeToolPayload(3_000))
        };

        var result = await compactor.ProjectAsync(
            Request(messages, keepRecentToolResults: 0, minMaskSavings: 1),
            CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Equal(0, result.MaskedCount);
        Assert.DoesNotContain(
            result.Messages,
            message => message.Content?.Contains("\"compacted\":true", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ProjectAsync_BelowMinSavings_DoesNothing()
    {
        var summarizer = new FakeSummarizer();
        var compactor = new AgentContextCompactor(summarizer);
        var messages = new List<AgentMessage>();
        for (var index = 0; index < 4; index++)
        {
            messages.Add(AssistantCall("call_" + index, "get_test"));
            messages.Add(AgentMessage.ToolResult(
                "call_" + index, LargeToolPayload(12_000)));
        }

        var result = await compactor.ProjectAsync(
            Request(messages, keepRecentToolResults: 0, minMaskSavings: 100_000),
            CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Equal(0, result.MaskedCount);
        Assert.DoesNotContain(
            result.Messages,
            message => message.Content?.Contains("\"compacted\":true", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ProjectAsync_IsIdempotent()
    {
        var summarizer = new FakeSummarizer();
        var compactor = new AgentContextCompactor(summarizer);
        var messages = new List<AgentMessage>();
        for (var index = 0; index < 4; index++)
        {
            messages.Add(AssistantCall("call_" + index, "get_test"));
            messages.Add(AgentMessage.ToolResult(
                "call_" + index, LargeToolPayload(12_000)));
        }

        var first = await compactor.ProjectAsync(
            Request(messages, keepRecentToolResults: 0, minMaskSavings: 1),
            CancellationToken.None);
        var second = await compactor.ProjectAsync(
            Request(first.Messages, keepRecentToolResults: 0, minMaskSavings: 1),
            CancellationToken.None);

        Assert.Equal(4, first.MaskedCount);
        Assert.Equal(0, second.MaskedCount);
        Assert.False(second.Changed);
    }

    [Fact]
    public async Task Summary_SummarizesOnlyThePrefixAndKeepsTheTail()
    {
        var summarizer = new FakeSummarizer { Result = "CHECKPOINT" };
        var compactor = new AgentContextCompactor(summarizer);
        var messages = new List<AgentMessage>();
        for (var index = 0; index < 8; index++)
        {
            messages.Add(AgentMessage.User(
                "PREFIX-FACT-" + index + " " + new string('я', 4000)));
            messages.Add(AgentMessage.Assistant("ответ " + index));
        }

        messages.Add(AgentMessage.User("TAIL-FACT " + new string('я', 4000)));

        var result = await compactor.CompactAsync(
            Request(messages, keepRecentMessages: 1),
            CancellationToken.None);

        Assert.True(result.Summarized);
        Assert.NotEmpty(summarizer.Inputs);
        var input = string.Join("\n", summarizer.Inputs);
        Assert.Contains("PREFIX-FACT-0", input, StringComparison.Ordinal);
        Assert.DoesNotContain("TAIL-FACT", input, StringComparison.Ordinal);
        Assert.Contains(
            result.Messages,
            message => (message.Content ?? string.Empty).Contains(
                "TAIL-FACT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Summary_UserMessagesAreVerbatim()
    {
        var summarizer = new FakeSummarizer { Result = "CHECKPOINT" };
        var compactor = new AgentContextCompactor(summarizer);
        var verbatim = "USER-VERBATIM-START " + new string('я', 10_000) +
                       " USER-VERBATIM-END";
        var messages = new List<AgentMessage>
        {
            AgentMessage.User(verbatim),
            AgentMessage.Assistant("принято")
        };
        for (var index = 0; index < 6; index++)
        {
            messages.Add(AgentMessage.User("шаг " + index + " " + new string('я', 4000)));
            messages.Add(AgentMessage.Assistant("ответ " + index));
        }

        var result = await compactor.CompactAsync(
            Request(messages, keepRecentMessages: 1),
            CancellationToken.None);

        Assert.True(result.Summarized);
        var input = string.Join("\n", summarizer.Inputs);
        Assert.Contains(verbatim, input, StringComparison.Ordinal);
        Assert.Contains("USER-VERBATIM-END", input, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Checkpoint_UsesTheTokenTailBeyondTheMessageCount()
    {
        var summarizer = new FakeSummarizer { Result = "CHECKPOINT" };
        var compactor = new AgentContextCompactor(summarizer);
        var messages = new List<AgentMessage>();
        for (var index = 0; index < 20; index++)
            messages.Add(AgentMessage.User("шаг " + index + " " + new string('я', 1000)));

        var result = await compactor.CompactAsync(
            Request(messages, keepRecentMessages: 1, keepRecentTokens: 2_000),
            CancellationToken.None);

        Assert.True(result.Summarized);
        Assert.True(
            result.Messages.Count > 2,
            "the token tail must keep more than the single recent message");
        Assert.True(
            result.Messages.Count < messages.Count,
            "the prefix must still be summarized");
    }

    [Fact]
    public async Task ReserveTokens_LowerTheTrigger()
    {
        var messages = new List<AgentMessage>();
        for (var index = 0; index < 7; index++)
            messages.Add(AgentMessage.User("шаг " + index + " " + new string('я', 1000)));

        var summarizer = new FakeSummarizer { Result = "CHECKPOINT" };
        var compactor = new AgentContextCompactor(summarizer);

        var withoutReserve = await compactor.CompactAsync(
            Request(messages, keepRecentMessages: 1),
            CancellationToken.None);
        var withReserve = await compactor.CompactAsync(
            Request(messages, keepRecentMessages: 1, reserveTokens: 2_000),
            CancellationToken.None);

        Assert.False(withoutReserve.Changed);
        Assert.True(withReserve.Summarized);
    }

    [Fact]
    public async Task SummaryFailure_HardShrinkKeepsTheCheckpoint()
    {
        var summarizer = new FakeSummarizer { Failure = new InvalidOperationException("boom") };
        var compactor = new AgentContextCompactor(summarizer);
        var messages = new List<AgentMessage>
        {
            AgentMessage.System(
                "<conversation_checkpoint>\nKEEP-ME\n</conversation_checkpoint>")
        };
        for (var index = 0; index < 8; index++)
            messages.Add(AgentMessage.User("шаг " + index + " " + new string('я', 4000)));

        var result = await compactor.CompactAsync(
            Request(messages, keepRecentMessages: 2),
            CancellationToken.None);

        Assert.True(result.Fallback);
        Assert.Contains(
            result.Messages,
            message => (message.Content ?? string.Empty).Contains(
                "KEEP-ME", StringComparison.Ordinal));
    }

    [Fact]
    public void CheckpointValidation_UsesOnlyTheSourceSlice()
    {
        var history = new List<AgentMessage>();
        for (var index = 0; index < 10; index++)
            history.Add(AgentMessage.User("m" + index).WithSessionIndex(index + 1));

        var source = history.Skip(4).Take(4).ToList();
        var valid = new AgentCompactionRecord
        {
            Id = "valid",
            SessionId = Session,
            FromRowId = 5,
            ToRowId = 8,
            Summary = "summary",
            SourcePrefixHash = AgentCompactionHash.Compute(source)
        };
        var fullPrefixHash = new AgentCompactionRecord
        {
            Id = "wrong",
            SessionId = Session,
            FromRowId = 5,
            ToRowId = 8,
            Summary = "summary",
            SourcePrefixHash = AgentCompactionHash.Compute(history.Take(8).ToList())
        };

        Assert.True(AgentCompactionHash.Validate(valid, history));
        Assert.False(AgentCompactionHash.Validate(fullPrefixHash, history));
    }

    [Fact]
    public async Task Masking_PreservesSessionIndex()
    {
        var summarizer = new FakeSummarizer();
        var compactor = new AgentContextCompactor(summarizer);
        var messages = new List<AgentMessage>();
        for (var index = 0; index < 4; index++)
        {
            messages.Add(AssistantCall("call_" + index, "get_test"));
            messages.Add(AgentMessage
                .ToolResult("call_" + index, LargeToolPayload(12_000))
                .WithSessionIndex(index * 2 + 2));
        }

        var result = await compactor.ProjectAsync(
            Request(messages, keepRecentToolResults: 0, minMaskSavings: 1),
            CancellationToken.None);

        var masked = result.Messages.First(message => message.Role == AgentRole.Tool);
        Assert.Equal(2, masked.SessionIndex);
        Assert.Equal(4, result.MaskedCount);
    }

    [Fact]
    public async Task Summary_HashesRawHistoryAfterProjection()
    {
        var raw = new List<AgentMessage>
        {
            AssistantCall("call_0", "get_test").WithSessionIndex(1),
            AgentMessage
                .ToolResult("call_0", LargeToolPayload(20_000))
                .WithSessionIndex(2)
        };
        for (var index = 0; index < 8; index++)
        {
            raw.Add(AgentMessage
                .User("PREFIX " + index + " " + new string('я', 4000))
                .WithSessionIndex(raw.Count + 1));
            raw.Add(AgentMessage
                .Assistant("ответ " + index)
                .WithSessionIndex(raw.Count + 1));
        }

        var projected = await new AgentContextCompactor(new FakeSummarizer())
            .ProjectAsync(
                Request(raw, keepRecentToolResults: 0, minMaskSavings: 1),
                CancellationToken.None);
        Assert.Contains(
            projected.Messages,
            message => message.Content?.Contains(
                "\"compacted\":true", StringComparison.Ordinal) == true);

        var store = new InMemoryCompactionStore();
        var compactor = new AgentContextCompactor(
            new FakeSummarizer { Result = "CHECKPOINT" }, store: store);
        var result = await compactor.CompactAsync(
            Request(projected.Messages, keepRecentMessages: 2, rawHistory: raw),
            CancellationToken.None);

        Assert.True(result.Summarized);
        var saved = Assert.Single(store.Saved);
        Assert.True(AgentCompactionHash.Validate(saved, raw));
    }

    [Fact]
    public async Task SecondSummary_UsesTheAdvancedAnchor()
    {
        var raw = new List<AgentMessage>();
        for (var index = 0; index < 10; index++)
        {
            raw.Add(AgentMessage
                .User("first " + index + " " + new string('я', 4000))
                .WithSessionIndex(index + 1));
        }

        var store = new InMemoryCompactionStore();
        var firstCompactor = new AgentContextCompactor(
            new FakeSummarizer { Result = "FIRST" }, store: store);
        var first = await firstCompactor.CompactAsync(
            Request(raw, keepRecentMessages: 2, rawHistory: raw),
            CancellationToken.None);

        Assert.True(first.Summarized);
        Assert.True(first.CoveredToRowId > 0);
        Assert.True(AgentCompactionHash.Validate(store.Saved[0], raw));

        var working = first.Messages;
        for (var index = 0; index < 6; index++)
        {
            raw.Add(AgentMessage
                .User("second " + index + " " + new string('я', 4000))
                .WithSessionIndex(raw.Count + 1));
            working.Add(raw[raw.Count - 1]);
        }

        var secondCompactor = new AgentContextCompactor(
            new FakeSummarizer { Result = "SECOND" }, store: store);
        var second = await secondCompactor.CompactAsync(
            Request(
                working,
                keepRecentMessages: 1,
                baseRowId: first.CoveredToRowId,
                rawHistory: raw),
            CancellationToken.None);

        Assert.True(second.Summarized);
        Assert.True(second.CoveredToRowId > first.CoveredToRowId);
        Assert.True(AgentCompactionHash.Validate(store.Saved[store.Saved.Count - 1], raw));
    }

    [Fact]
    public async Task PostEstimator_DoesNotLeakTheTriggerFloor()
    {
        var summarizer = new FakeSummarizer();
        var compactor = new AgentContextCompactor(summarizer);
        var messages = new List<AgentMessage>();
        for (var index = 0; index < 3; index++)
        {
            messages.Add(AssistantCall("call_" + index, "get_test"));
            messages.Add(AgentMessage.ToolResult(
                "call_" + index, LargeToolPayload(12_000)));
        }

        var result = await compactor.CompactAsync(
            Request(
                messages,
                keepRecentToolResults: 0,
                postEstimator: _ => 2_000),
            CancellationToken.None);

        Assert.False(result.Summarized);
        Assert.True(result.MaskedCount > 0);
        Assert.True(result.TokensAfter <= 3_000);
    }

    [Fact]
    public async Task CheckpointDisabled_HardShrinksWithoutPersisting()
    {
        var store = new InMemoryCompactionStore();
        var compactor = new AgentContextCompactor(new FakeSummarizer(), store: store);

        var result = await compactor.CompactAsync(
            Request(
                BuildLargeHistory(),
                keepRecentMessages: 2,
                allowCheckpoint: false),
            CancellationToken.None);

        Assert.True(result.Fallback);
        Assert.Equal("summary_disabled", result.FallbackReason);
        Assert.Empty(store.Saved);
        Assert.True(result.DroppedCount > 0);
    }

    [Fact]
    public void CheckpointHash_ChangesWhenTheSourceChanges()
    {
        var first = new List<AgentMessage>
        {
            AgentMessage.User("a").WithSessionIndex(1),
            AgentMessage.Assistant("b").WithSessionIndex(2)
        };
        var second = new List<AgentMessage>
        {
            AgentMessage.User("a").WithSessionIndex(1),
            AgentMessage.Assistant("changed").WithSessionIndex(2)
        };

        Assert.NotEqual(
            AgentCompactionHash.Compute(first),
            AgentCompactionHash.Compute(second));
    }
}
