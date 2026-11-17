using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Interfaces.Agent;

public interface IAgentProjectContextProvider
{
    Task<AgentProjectContext> GetAsync(CancellationToken ct);
}