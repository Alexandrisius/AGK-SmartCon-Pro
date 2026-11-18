using System.Text.Json;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Interfaces.Agent;

/// <summary>
/// Optional L1 preflight implemented by tools whose argument checks are cheaper
/// than a full dry run (id existence, view family compatibility, limits).
/// </summary>
public interface IAgentToolPreflight
{
    Task<PlanPreflightResult> PreflightAsync(
        AgentToolContext context,
        JsonElement args,
        CancellationToken ct);
}
