using System.Text.Json;
using SmartCon.Agent.Services.Llm;
using SmartCon.Core.Models.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Llm;

/// <summary>
/// P.5: per-conversation cache namespace and provider-specific cache hints,
/// grounded in the 2026 OpenAI / Anthropic / DeepSeek / Gemini / OpenRouter docs.
/// </summary>
public sealed class AgentProviderCachePolicyTests
{
    private static readonly Guid ConversationId =
        Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly string ConversationKey =
        ConversationId.ToString("N");

    [Fact]
    public void OpenAiModel_UsesPromptCacheKey()
    {
        var plan = AgentProviderCachePolicy.Resolve(
            "openrouter", "openai/gpt-5.5", 0.2, ConversationId);

        Assert.Equal(AgentCacheKind.PromptCacheKey, plan.Kind);
        Assert.Equal(ConversationKey, plan.PromptCacheKey);
        Assert.False(plan.UseAnthropicBreakpoints);
        Assert.Equal("ses_" + ConversationKey, plan.ProviderOptions["session_id"]);
    }

    [Fact]
    public void AnthropicModel_UsesEphemeralBreakpoints()
    {
        var plan = AgentProviderCachePolicy.Resolve(
            "openrouter", "anthropic/claude-sonnet-4.5", 0.2, ConversationId);

        Assert.Equal(AgentCacheKind.EphemeralBreakpoints, plan.Kind);
        Assert.True(plan.UseAnthropicBreakpoints);
        Assert.Null(plan.PromptCacheKey);
        Assert.Equal("ses_" + ConversationKey, plan.ProviderOptions["session_id"]);
    }

    [Fact]
    public void DeepSeekAndGemini_AreImplicitOnly()
    {
        var deepSeek = AgentProviderCachePolicy.Resolve(
            "openrouter", "deepseek/deepseek-v4.1-flash", 0.2, ConversationId);
        var gemini = AgentProviderCachePolicy.Resolve(
            "openrouter", "google/gemini-2.5-pro", 0.2, ConversationId);

        Assert.Equal(AgentCacheKind.Implicit, deepSeek.Kind);
        Assert.Null(deepSeek.PromptCacheKey);
        Assert.False(deepSeek.UseAnthropicBreakpoints);
        Assert.Equal(AgentCacheKind.Implicit, gemini.Kind);
        Assert.Null(gemini.PromptCacheKey);
        Assert.False(gemini.UseAnthropicBreakpoints);
    }

    [Fact]
    public void OpenCodeGo_SendsPerConversationSessionValue()
    {
        var plan = AgentProviderCachePolicy.Resolve(
            "opencode-go", "deepseek-v4.1-flash", 0.2, ConversationId);

        Assert.Equal("ses_" + ConversationKey, plan.OpenCodeSession);
        Assert.Null(plan.PromptCacheKey);
        Assert.Empty(plan.ProviderOptions);
    }

    [Fact]
    public void UnknownProvider_IsFailClosed()
    {
        var plan = AgentProviderCachePolicy.Resolve(
            "mystery", "some-model", 0.2, ConversationId);

        Assert.Equal(AgentCacheKind.None, plan.Kind);
        Assert.Null(plan.PromptCacheKey);
        Assert.Null(plan.OpenCodeSession);
        Assert.False(plan.UseAnthropicBreakpoints);
        Assert.Empty(plan.ProviderOptions);
    }

    [Fact]
    public void CacheKey_FallsBackToConversationId_AndIsBounded()
    {
        Assert.Equal(
            ConversationKey,
            AgentProviderCachePolicy.BuildCacheKey(null, ConversationId));
        Assert.Equal(
            "explicit-key",
            AgentProviderCachePolicy.BuildCacheKey("explicit-key", ConversationId));
        Assert.Equal(
            AgentProviderCachePolicy.MaxSessionIdLength,
            AgentProviderCachePolicy.BuildCacheKey(new string('k', 400), null).Length);

        var longKey = AgentProviderCachePolicy.Resolve(
            "openai", "gpt-5.5", 0.2, ConversationId, new string('k', 400));
        Assert.Equal(AgentProviderCachePolicy.MaxCacheKeyLength, longKey.PromptCacheKey!.Length);
    }

    [Fact]
    public void Namespace_ChangesWithModelOrEffort()
    {
        var baseNamespace = AgentProviderCachePolicy.BuildNamespace("openrouter", "deepseek/x", 0.2);
        var otherModel = AgentProviderCachePolicy.BuildNamespace("openrouter", "deepseek/y", 0.2);
        var otherEffort = AgentProviderCachePolicy.BuildNamespace("openrouter", "deepseek/x", 0.7);

        Assert.NotEqual(baseNamespace, otherModel);
        Assert.NotEqual(baseNamespace, otherEffort);
        Assert.Equal(
            baseNamespace,
            AgentProviderCachePolicy.BuildNamespace("OpenRouter", "deepseek/x", 0.2));
    }

    [Fact]
    public void NamespaceChange_IsLoggedOnlyWhenTheNamespaceDiffers()
    {
        var first = AgentProviderCachePolicy.BuildNamespace("openrouter", "deepseek/x", 0.2);
        var second = AgentProviderCachePolicy.BuildNamespace("openrouter", "deepseek/y", 0.2);

        Assert.Null(AgentProviderCachePolicy.DescribeNamespaceChange(null, first));
        Assert.Null(AgentProviderCachePolicy.DescribeNamespaceChange(first, first));

        var message = AgentProviderCachePolicy.DescribeNamespaceChange(first, second);
        Assert.NotNull(message);
        Assert.StartsWith("Agent cache namespace changed: ", message, StringComparison.Ordinal);
        Assert.Contains(first, message, StringComparison.Ordinal);
        Assert.Contains(second, message, StringComparison.Ordinal);
    }

    [Fact]
    public void Payload_MarksOnlyTheStableSystemBlocksForAnthropic()
    {
        var request = Request("anthropic/claude-sonnet-4.5");
        var plan = AgentProviderCachePolicy.Resolve(
            "openrouter", request.Model, 0.2, ConversationId);

        var payload = LlmPayloadBuilder.Build(request, plan);
        var json = JsonSerializer.Serialize(payload);

        Assert.Equal(2, CountOccurrences(json, "\"cache_control\""));
        Assert.DoesNotContain("\"prompt_cache_key\"", json, StringComparison.Ordinal);
        Assert.Contains("\"session_id\":\"ses_" + ConversationKey + "\"", json, StringComparison.Ordinal);

        var messages = (List<object>)payload["messages"]!;
        var turnContext = (Dictionary<string, object?>)messages[2];
        Assert.IsType<string>(turnContext["content"]);
    }

    [Fact]
    public void Payload_OmitsAnthropicMarkersForDeepSeek()
    {
        var request = Request("deepseek/deepseek-v4.1-flash");
        var plan = AgentProviderCachePolicy.Resolve(
            "openrouter", request.Model, 0.2, ConversationId);

        var json = JsonSerializer.Serialize(LlmPayloadBuilder.Build(request, plan));

        Assert.DoesNotContain("\"cache_control\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"prompt_cache_key\"", json, StringComparison.Ordinal);
        Assert.Contains("\"session_id\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Payload_MergesCallerProviderOptionsLast()
    {
        var request = new AgentChatRequest
        {
            Model = "openai/gpt-5.5",
            Messages = new[] { AgentMessage.User("привет") },
            ProviderOptions = new Dictionary<string, object?>
            {
                ["session_id"] = "caller-session"
            }
        };
        var plan = AgentProviderCachePolicy.Resolve(
            "openrouter", request.Model, 0.2, ConversationId);

        var payload = LlmPayloadBuilder.Build(request, plan);

        Assert.Equal("caller-session", payload["session_id"]);
    }

    private static AgentChatRequest Request(string model) =>
        new()
        {
            Model = model,
            Messages = new[]
            {
                AgentMessage.System("<static prompt>"),
                AgentMessage.System("<session_context>\ndocument_id: doc\n</session_context>"),
                AgentMessage.System("<turn_context>\n</turn_context>"),
                AgentMessage.User("привет")
            },
            ConversationId = ConversationId
        };

    private static int CountOccurrences(string value, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }
}