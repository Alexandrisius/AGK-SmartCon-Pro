using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Interfaces.Agent;

/// <summary>
/// Human approval gate for write/destructive agent tools (mvp-plan §9.2, §9.8 L3).
/// Implementations must never auto-approve in production; tests use stubs.
/// </summary>
public interface IAgentConfirmationService
{
    Task<AgentConfirmationResult> RequestAsync(
        AgentConfirmationRequest request,
        CancellationToken ct);
}
