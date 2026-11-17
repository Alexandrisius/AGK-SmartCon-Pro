using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services;

/// <summary>Provider/model of the active client, reported with tooling feedback.</summary>
public sealed class AgentRuntimeInfo : IAgentRuntimeInfo
{
    private readonly IAgentLlmClient _client;

    public AgentRuntimeInfo(IAgentLlmClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public string Provider => _client.ProviderName;

    public string Model => _client.Model;
}
