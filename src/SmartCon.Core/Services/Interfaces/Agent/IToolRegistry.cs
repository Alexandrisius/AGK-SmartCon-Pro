using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Interfaces.Agent;

public interface IToolRegistry
{
    IReadOnlyList<IAgentTool> GetAll();

    /// <summary>
    /// Definitions served to the LLM (catalog metadata applied). Routing and the
    /// find_tools meta-tool work on this projection.
    /// </summary>
    IReadOnlyList<AgentToolDefinition> GetDefinitions();

    bool TryGet(string name, out IAgentTool? tool);
}

public interface IToolExecutor
{
    Task<ToolResult> ExecuteAsync(string name, System.Text.Json.JsonElement args, CancellationToken ct);
}