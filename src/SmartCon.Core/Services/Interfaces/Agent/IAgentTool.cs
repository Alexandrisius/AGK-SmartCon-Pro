using System.Text.Json;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Interfaces.Agent;

public interface IAgentTool
{
    AgentToolDefinition Definition { get; }

    Task<ToolResult> ExecuteAsync(AgentToolContext context, JsonElement args, CancellationToken ct);
}