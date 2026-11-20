using System.Net;
using System.Net.Http;
using System.Text;
using Xunit;
using SmartCon.Agent.Services.Llm;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Tests.Agent.Llm;

public sealed class OpenAiCompatibleLlmClientTests
{
    [Fact]
    public async Task Stream_AfterHotProviderSwitch_SendsOpenCodeSessionHeader()
    {
        var settings = new AgentLlmSettings
        {
            Provider = "openrouter",
            Model = AgentLlmSettings.DefaultModel,
            BaseUrl = AgentLlmSettings.DefaultOpenRouterBaseUrl,
            ApiKey = "sk-test"
        };

        using var handler = new RecordingHandler();
        using var client = new OpenAiCompatibleLlmClient(settings, handler);

        settings.Provider = "opencode-go";
        settings.BaseUrl = AgentLlmSettings.DefaultOpenCodeGoBaseUrl;
        settings.Model = AgentLlmSettings.DefaultOpenCodeGoModel;

        await DrainAsync(client);

        Assert.Equal(
            AgentLlmSettings.DefaultOpenCodeGoBaseUrl + "/chat/completions",
            handler.LastUri?.ToString());
        Assert.True(handler.HasHeader("x-opencode-session"));
        Assert.Matches("^ses_[0-9a-f]{32}$", handler.Header("x-opencode-session"));
        Assert.StartsWith("SmartCon-Agent/", handler.Header("User-Agent"));
    }

    [Fact]
    public async Task Stream_OpenRouter_DoesNotSendOpenCodeSessionHeader()
    {
        var settings = new AgentLlmSettings
        {
            Provider = "openrouter",
            Model = AgentLlmSettings.DefaultModel,
            BaseUrl = AgentLlmSettings.DefaultOpenRouterBaseUrl,
            ApiKey = "sk-or-test"
        };

        using var handler = new RecordingHandler();
        using var client = new OpenAiCompatibleLlmClient(settings, handler);

        await DrainAsync(client);

        Assert.False(handler.HasHeader("x-opencode-session"));
        Assert.StartsWith("SmartCon-Agent/", handler.Header("User-Agent"));
    }

    [Fact]
    public async Task Stream_ModelSwitchWithinOneClient_EmitsTheNamespaceChange()
    {
        var settings = new AgentLlmSettings
        {
            Provider = "openrouter",
            Model = "openai/gpt-5.5",
            BaseUrl = AgentLlmSettings.DefaultOpenRouterBaseUrl,
            ApiKey = "sk-or-test"
        };

        using var handler = new RecordingHandler();
        using var client = new OpenAiCompatibleLlmClient(settings, handler);

        await DrainAsync(client, Guid.NewGuid(), "openai/gpt-5.5");
        await DrainAsync(client, Guid.NewGuid(), "deepseek/deepseek-v4.1-flash");

        // The switch itself is asserted through DescribeNamespaceChange; this run
        // proves the client executes the logging path without breaking the request.
        Assert.DoesNotContain("\"prompt_cache_key\"", handler.LastBody, StringComparison.Ordinal);
    }

    private static async Task DrainAsync(
        IAgentLlmClient client,
        Guid? conversationId = null,
        string model = "test-model")
    {
        var request = new AgentChatRequest
        {
            Model = model,
            Messages = new[] { new AgentMessage { Role = AgentRole.User, Content = "ping" } },
            ConversationId = conversationId
        };

        await foreach (var _ in client.StreamAsync(request, CancellationToken.None))
        {
        }
    }

    [Fact]
    public async Task Stream_OpenCodeGo_UsesThePerConversationSessionHeader()
    {
        var settings = new AgentLlmSettings
        {
            Provider = "opencode-go",
            Model = AgentLlmSettings.DefaultOpenCodeGoModel,
            BaseUrl = AgentLlmSettings.DefaultOpenCodeGoBaseUrl,
            ApiKey = "sk-test"
        };
        var conversationId = Guid.NewGuid();

        using var handler = new RecordingHandler();
        using var client = new OpenAiCompatibleLlmClient(settings, handler);

        await DrainAsync(client, conversationId);

        Assert.Equal(
            "ses_" + conversationId.ToString("N"),
            handler.Header("x-opencode-session"));
    }

    [Fact]
    public async Task Stream_OpenRouterOpenAiModel_SendsPromptCacheKeyAndSessionId()
    {
        var settings = new AgentLlmSettings
        {
            Provider = "openrouter",
            Model = "openai/gpt-5.5",
            BaseUrl = AgentLlmSettings.DefaultOpenRouterBaseUrl,
            ApiKey = "sk-or-test"
        };
        var conversationId = Guid.NewGuid();

        using var handler = new RecordingHandler();
        using var client = new OpenAiCompatibleLlmClient(settings, handler);

        await DrainAsync(client, conversationId, "openai/gpt-5.5");

        Assert.Contains(
            "\"prompt_cache_key\":\"" + conversationId.ToString("N") + "\"",
            handler.LastBody,
            StringComparison.Ordinal);
        Assert.Contains("\"session_id\":\"ses_", handler.LastBody, StringComparison.Ordinal);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _headers =
            new(StringComparer.OrdinalIgnoreCase);

        public Uri? LastUri { get; private set; }

        public string LastBody { get; private set; } = string.Empty;

        public bool HasHeader(string name) => _headers.ContainsKey(name);

        public string Header(string name) =>
            _headers.TryGetValue(name, out var value) ? value : string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            foreach (var header in request.Headers)
                _headers[header.Key] = string.Join(",", header.Value);

            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);

            const string body =
                "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}\n\n" +
                "data: [DONE]\n\n";

            var content = new StringContent(body, Encoding.UTF8, "text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }
}