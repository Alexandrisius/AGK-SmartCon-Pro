using Xunit;
using SmartCon.Agent.Services.Llm;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Tests.Agent.Llm;

public sealed class LlmResponseAccumulatorTests
{
    [Fact]
    public void Append_TextReasoningAndToolCall_BuildsCompleteResponse()
    {
        var accumulator = new LlmResponseAccumulator();
        var output = new List<LlmStreamEvent>();

        accumulator.Append(
            """{"choices":[{"delta":{"reasoning_content":"think-"}}]}""",
            output);
        accumulator.Append(
            """{"choices":[{"delta":{"reasoning_content":"more","content":"Привет"}}]}""",
            output);
        accumulator.Append(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"list_views","arguments":"{\"limit\":"}}]}}]}""",
            output);
        accumulator.Append(
            """{"usage":{"prompt_tokens":5,"completion_tokens":2,"total_tokens":7,"completion_tokens_details":{"reasoning_tokens":1},"cost":0.0001},"choices":[]}""",
            output);

        var response = accumulator.Build("openrouter", "test-model");

        Assert.Equal("Привет", response.Message.Content);
        Assert.Equal("think-more", response.Message.ReasoningContent);
        Assert.Single(response.Message.ToolCalls);
        Assert.Equal("call_1", response.Message.ToolCalls[0].Id);
        Assert.Equal("list_views", response.Message.ToolCalls[0].Name);
        Assert.Equal("{\"limit\":", response.Message.ToolCalls[0].ArgumentsJson);
        Assert.Equal(7, response.Usage.TotalTokens);
        Assert.Equal(1, response.Usage.ReasoningTokens);
        Assert.Equal(0.0001m, response.Usage.CostUsd!.Value);
        Assert.Contains(output, item => item.Kind == LlmStreamEventKind.TextDelta);
        Assert.Contains(output, item => item.Kind == LlmStreamEventKind.ToolCallStarted);
    }

    [Fact]
    public void Append_CumulativeArgumentDeltas_ReplacesInsteadOfDuplicating()
    {
        var accumulator = new LlmResponseAccumulator();
        var output = new List<LlmStreamEvent>();

        accumulator.Append(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"list_views","arguments":"{\"limit\":"}}]}}]}""",
            output);
        accumulator.Append(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"limit\":10}"}}]}}]}""",
            output);

        var response = accumulator.Build("openrouter", "test-model");

        Assert.Equal("{\"limit\":10}", response.Message.ToolCalls[0].ArgumentsJson);
    }

    [Fact]
    public void Append_ToolCallWithoutId_GeneratesStableFallbackId()
    {
        var accumulator = new LlmResponseAccumulator();
        var output = new List<LlmStreamEvent>();

        accumulator.Append(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"name":"list_views","arguments":"{}"}}]}}]}""",
            output);

        var started = Assert.Single(
            output,
            item => item.Kind == LlmStreamEventKind.ToolCallStarted);
        var response = accumulator.Build("openrouter", "test-model");
        var id = response.Message.ToolCalls[0].Id;

        // The suffix makes the id unique per response: a plain "call_0" collided
        // across conversations and overwrote stored tool-result blobs.
        Assert.StartsWith("call_0_", id, StringComparison.Ordinal);
        Assert.Equal(id, started.ToolCallId);

        var other = new LlmResponseAccumulator();
        other.Append(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"name":"list_views","arguments":"{}"}}]}}]}""",
            new List<LlmStreamEvent>());
        Assert.NotEqual(id, other.Build("openrouter", "test-model").Message.ToolCalls[0].Id);
    }

    [Fact]
    public void Append_NestedPromptTokenDetails_ParsesCachedAndCacheWrite()
    {
        var accumulator = new LlmResponseAccumulator();
        var output = new List<LlmStreamEvent>();

        accumulator.Append(
            """{"usage":{"prompt_tokens":100,"completion_tokens":10,"total_tokens":110,"prompt_cache_hit_tokens":99,"prompt_tokens_details":{"cached_tokens":40,"cache_write_tokens":15}},"choices":[]}""",
            output);

        var response = accumulator.Build("openrouter", "test-model");

        Assert.Equal(40, response.Usage.CachedTokens);
        Assert.Equal(15, response.Usage.CacheWriteTokens);
        Assert.Equal(100, response.Usage.PromptTokens);
    }

    [Fact]
    public void Append_DeepSeekTopLevelPromptCacheFields_FallBackWhenNestedMissing()
    {
        var accumulator = new LlmResponseAccumulator();
        var output = new List<LlmStreamEvent>();

        accumulator.Append(
            """{"usage":{"prompt_tokens":200,"completion_tokens":20,"total_tokens":220,"prompt_cache_hit_tokens":64,"prompt_cache_write_tokens":12},"choices":[]}""",
            output);

        var response = accumulator.Build("deepseek", "deepseek-chat");

        Assert.Equal(64, response.Usage.CachedTokens);
        Assert.Equal(12, response.Usage.CacheWriteTokens);
    }

    [Fact]
    public void Append_RootId_KeepsLastNonEmptyRequestId()
    {
        var accumulator = new LlmResponseAccumulator();
        var output = new List<LlmStreamEvent>();

        accumulator.Append(
            """{"id":"req-1","choices":[{"delta":{"content":"a"}}]}""",
            output);
        accumulator.Append(
            """{"id":"req-2","choices":[{"delta":{"content":"b"}}]}""",
            output);
        accumulator.Append(
            """{"id":"","choices":[{"delta":{"content":"c"}}]}""",
            output);

        var response = accumulator.Build("openrouter", "test-model");

        Assert.Equal("req-2", response.RequestId);
    }

    [Fact]
    public void Append_CachedTokensAbovePromptTokens_IsClamped()
    {
        var accumulator = new LlmResponseAccumulator();
        var output = new List<LlmStreamEvent>();

        accumulator.Append(
            """{"usage":{"prompt_tokens":50,"completion_tokens":5,"total_tokens":55,"prompt_tokens_details":{"cached_tokens":80}},"choices":[]}""",
            output);

        var response = accumulator.Build("openrouter", "test-model");

        Assert.Equal(50, response.Usage.CachedTokens);
    }

    [Fact]
    public void Append_CacheWriteTokensAbovePromptTokens_IsClamped()
    {
        var accumulator = new LlmResponseAccumulator();
        var output = new List<LlmStreamEvent>();

        accumulator.Append(
            """{"usage":{"prompt_tokens":50,"completion_tokens":5,"total_tokens":55,"prompt_tokens_details":{"cache_write_tokens":80}},"choices":[]}""",
            output);

        var response = accumulator.Build("openrouter", "test-model");

        Assert.Equal(50, response.Usage.CacheWriteTokens);
    }

    [Fact]
    public void Append_TwoCallsWithoutIndexInOneDelta_StaySeparate()
    {
        var accumulator = new LlmResponseAccumulator();
        var output = new List<LlmStreamEvent>();

        accumulator.Append(
            """{"choices":[{"delta":{"tool_calls":[{"id":"call_a","function":{"name":"undo_last","arguments":"{}"}},{"id":"call_b","function":{"name":"rename_levels","arguments":"{\"items\":[]}"}}]}}]}""",
            output);

        var response = accumulator.Build("openrouter", "test-model");

        Assert.Equal(2, response.Message.ToolCalls.Count);
        Assert.Equal("undo_last", response.Message.ToolCalls[0].Name);
        Assert.Equal("{}", response.Message.ToolCalls[0].ArgumentsJson);
        Assert.Equal("rename_levels", response.Message.ToolCalls[1].Name);
        Assert.Equal("{\"items\":[]}", response.Message.ToolCalls[1].ArgumentsJson);
    }

    [Fact]
    public void Append_TwoCallsWithoutIndexAcrossDeltas_StaySeparate()
    {
        var accumulator = new LlmResponseAccumulator();
        var output = new List<LlmStreamEvent>();

        accumulator.Append(
            """{"choices":[{"delta":{"tool_calls":[{"id":"call_a","function":{"name":"undo_last","arguments":"{"}}]}}]}""",
            output);
        accumulator.Append(
            """{"choices":[{"delta":{"tool_calls":[{"id":"call_a","function":{"arguments":"}"}}]}}]}""",
            output);
        accumulator.Append(
            """{"choices":[{"delta":{"tool_calls":[{"id":"call_b","function":{"name":"set_view_crop","arguments":"{\"viewId\":1}"}}]}}]}""",
            output);

        var response = accumulator.Build("openrouter", "test-model");

        Assert.Equal(2, response.Message.ToolCalls.Count);
        Assert.Equal("undo_last", response.Message.ToolCalls[0].Name);
        Assert.Equal("{}", response.Message.ToolCalls[0].ArgumentsJson);
        Assert.Equal("set_view_crop", response.Message.ToolCalls[1].Name);
        Assert.Equal("{\"viewId\":1}", response.Message.ToolCalls[1].ArgumentsJson);
    }
}