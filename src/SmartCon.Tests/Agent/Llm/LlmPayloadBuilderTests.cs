using Xunit;
using System.Text.Json;
using SmartCon.Agent.Services.Llm;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Tests.Agent.Llm;

public sealed class LlmPayloadBuilderTests
{
    [Fact]
    public void Build_AssistantToolCall_ReplaysReasoningContent()
    {
        var request = Request();

        var payload = LlmPayloadBuilder.Build(request);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var messages = document.RootElement.GetProperty("messages");

        var assistant = messages[2];
        Assert.Equal("assistant", assistant.GetProperty("role").GetString());
        Assert.Equal("think", assistant.GetProperty("reasoning_content").GetString());

        var toolCall = assistant.GetProperty("tool_calls")[0];
        Assert.Equal("call_1", toolCall.GetProperty("id").GetString());
        Assert.Equal("list_views", toolCall.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("{}", toolCall.GetProperty("function").GetProperty("arguments").GetString());
    }

    [Fact]
    public void Build_UserWithImage_UsesMultipartContent()
    {
        var request = Request();

        var payload = LlmPayloadBuilder.Build(request);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var content = document.RootElement.GetProperty("messages")[1].GetProperty("content");

        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());

        var url = content[1].GetProperty("image_url").GetProperty("url").GetString();
        Assert.NotNull(url);
        Assert.StartsWith("data:image/png;base64,", url, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ToolsAndStreamingOptions_ArePresent()
    {
        var request = Request();

        var payload = LlmPayloadBuilder.Build(request);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var root = document.RootElement;

        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.True(root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Equal("function", root.GetProperty("tools")[0].GetProperty("type").GetString());
        Assert.Equal("t", root.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("object", root.GetProperty("tools")[0].GetProperty("function")
            .GetProperty("parameters").GetProperty("type").GetString());
    }

    [Fact]
    public void Build_ReasoningEffortFormat_EmitsTopLevelEffort()
    {
        var request = Request();

        var payload = LlmPayloadBuilder.Build(
            request,
            reasoning: new AgentReasoningOptions(
                AgentReasoningFormat.ReasoningEffort, "max"));
        var json = JsonSerializer.Serialize(payload);

        Assert.Contains("\"reasoning_effort\":\"max\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"reasoning\":{\"effort\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ReasoningObjectFormat_EmitsEffortObject()
    {
        var request = Request();

        var payload = LlmPayloadBuilder.Build(
            request,
            reasoning: new AgentReasoningOptions(
                AgentReasoningFormat.ReasoningObject, "max"));
        var json = JsonSerializer.Serialize(payload);

        Assert.Contains("\"reasoning\":{\"effort\":\"max\"}", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"reasoning_effort\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WithoutReasoningOptions_EmitsNoReasoningFields()
    {
        var request = Request();

        var json = JsonSerializer.Serialize(LlmPayloadBuilder.Build(request));

        Assert.DoesNotContain("\"reasoning_effort\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"reasoning\":", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ReasoningSitsAfterMaxTokensAndBeforeCacheFields()
    {
        var request = Request();
        request = new AgentChatRequest
        {
            Model = request.Model,
            Messages = request.Messages,
            Tools = request.Tools,
            Temperature = request.Temperature,
            MaxOutputTokens = 64_000,
            ConversationId = Guid.NewGuid()
        };
        var plan = AgentProviderCachePolicy.Resolve(
            "openai", "gpt-5.5", 0.2, Guid.NewGuid());

        var json = JsonSerializer.Serialize(LlmPayloadBuilder.Build(
            request, plan,
            reasoning: new AgentReasoningOptions(
                AgentReasoningFormat.ReasoningEffort, "max")));

        var maxTokensAt = json.IndexOf("\"max_tokens\"", StringComparison.Ordinal);
        var reasoningAt = json.IndexOf("\"reasoning_effort\"", StringComparison.Ordinal);
        var cacheKeyAt = json.IndexOf("\"prompt_cache_key\"", StringComparison.Ordinal);

        Assert.True(maxTokensAt >= 0, "max_tokens expected");
        Assert.True(reasoningAt > maxTokensAt, "reasoning_effort must follow max_tokens");
        if (cacheKeyAt >= 0)
            Assert.True(reasoningAt < cacheKeyAt, "reasoning_effort must precede cache fields");
    }

    [Fact]
    public void Build_BudgetOverride_ReplacesOrOmitsMaxTokens()
    {
        var baseRequest = new AgentChatRequest
        {
            Model = "m",
            Messages = new[] { AgentMessage.User("hi") },
            MaxOutputTokens = 64_000
        };

        var halved = LlmPayloadBuilder.Build(baseRequest, maxOutputTokens: 32_000);
        Assert.Equal(32_000, halved["max_tokens"]);

        var omitted = LlmPayloadBuilder.Build(baseRequest, omitMaxOutputTokens: true);
        Assert.False(omitted.ContainsKey("max_tokens"));
    }

    private static AgentChatRequest Request() => new()
    {
        Model = "test-model",
        Messages = new[]
        {
            AgentMessage.System("system"),
            AgentMessage.User("hello", new[]
            {
                new AgentAttachment
                {
                    FileName = "screen.png",
                    MimeType = "image/png",
                    Base64Data = "AAAA"
                }
            }),
            AgentMessage.Assistant(
                null,
                new[]
                {
                    new AgentToolCall
                    {
                        Id = "call_1",
                        Name = "list_views",
                        ArgumentsJson = "{}"
                    }
                },
                "think"),
            AgentMessage.ToolResult("call_1", "{\"success\":true}")
        },
        Tools = new[]
        {
            new AgentToolDefinition
            {
                Name = "t",
                Description = "d",
                Risk = AgentToolRisk.Read,
                InputSchemaJson = "{\"type\":\"object\",\"properties\":{}}"
            }
        }
    };
}