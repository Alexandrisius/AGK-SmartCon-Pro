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

        var response = accumulator.Build("openrouter", "test-model");

        Assert.Equal("call_0", response.Message.ToolCalls[0].Id);
    }
}