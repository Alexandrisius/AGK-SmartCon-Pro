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