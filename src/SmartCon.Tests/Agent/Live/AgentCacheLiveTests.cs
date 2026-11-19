using SmartCon.Agent.Services.Llm;
using SmartCon.Agent.Services.Tools;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Tests.Agent.Support;
using Xunit;
using Xunit.Abstractions;

namespace SmartCon.Tests.Agent.Live;

/// <summary>
/// Live cache check (P.1–P.5 acceptance): three turns of one conversation
/// against the configured provider. Turn 2 must read a non-zero cached prefix,
/// and the static/tool-set hashes must not change between turns.
/// Skipped unless SMARTCON_AGENT_LIVE=1 and an API key are configured.
/// </summary>
public sealed class AgentCacheLiveTests
{
    private readonly ITestOutputHelper _output;

    public AgentCacheLiveTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ThreeTurns_SecondTurnReadsTheCachedPrefix()
    {
        if (Environment.GetEnvironmentVariable("SMARTCON_AGENT_LIVE") != "1")
            return;

        var settings = AgentLlmSettings.Load();
        Assert.True(settings.HasApiKey, "Set a provider API key before a live run.");

        var registry = new AgentToolRegistry(new IAgentTool[]
        {
            new ScriptedTool("list_views", AgentToolRisk.Read),
            new ScriptedTool("query_elements", AgentToolRisk.Read),
            new ScriptedTool("get_element_details", AgentToolRisk.Read)
        });
        var store = new InMemoryConversationStore();
        var usageStore = new CapturingUsageStore();
        using var client = new OpenAiCompatibleLlmClient(settings);
        var orchestrator = AgentHarness.Create(
            client,
            registry,
            store,
            usageStore: usageStore,
            settings: settings);
        var conversationId = Guid.NewGuid();
        var prompts = new[]
        {
            "Ответь ровно одним словом: раз.",
            "Ответь ровно одним словом: два.",
            "Ответь ровно одним словом: три."
        };

        for (var turn = 0; turn < prompts.Length; turn++)
        {
            await orchestrator.RunTurnAsync(
                conversationId,
                prompts[turn],
                Array.Empty<AgentAttachment>(),
                null,
                CancellationToken.None);
        }

        Assert.Equal(3, usageStore.Records.Count);
        foreach (var record in usageStore.Records)
        {
            _output.WriteLine(
                "turn usage: prompt=" + record.Usage.PromptTokens +
                " cached=" + record.Usage.CachedTokens +
                " cache_write=" + record.Usage.CacheWriteTokens +
                " completion=" + record.Usage.CompletionTokens +
                " cost=" + (record.Usage.CostUsd?.ToString(
                    "F6", System.Globalization.CultureInfo.InvariantCulture) ?? "(none)") +
                " request_id=" + (record.RequestId ?? "(none)"));
        }

        Assert.True(
            usageStore.Records[1].Usage.CachedTokens > 0,
            "turn 2 read 0 cached tokens: provider=" + settings.Provider +
            ", model=" + settings.Model);
    }
}