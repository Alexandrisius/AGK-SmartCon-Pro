using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Interfaces.Agent;

public interface IAgentAuditLog
{
    Task WriteAsync(AgentAuditEntry entry, CancellationToken ct);
}