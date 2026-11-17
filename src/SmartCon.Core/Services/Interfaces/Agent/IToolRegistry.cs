using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Interfaces.Agent;

public interface IToolRegistry
{
    IReadOnlyList<IAgentTool> GetAll();

    bool TryGet(string name, out IAgentTool? tool);
}

public interface IToolExecutor
{
    Task<ToolResult> ExecuteAsync(string name, System.Text.Json.JsonElement args, CancellationToken ct);
}