using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Interfaces.Agent;

public interface IAgentLlmClient
{
    string ProviderName { get; }

    string Model { get; }

    IAsyncEnumerable<LlmStreamEvent> StreamAsync(AgentChatRequest request, CancellationToken ct);
}