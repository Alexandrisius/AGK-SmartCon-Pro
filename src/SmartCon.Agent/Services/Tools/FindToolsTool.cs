using System.Text.Json;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services.Tools;

/// <summary>
/// Meta-tool that returns full definitions for tools matching a query. Used when
/// the LLM works with a routed shortlist and needs a tool outside it.
/// </summary>
public sealed class FindToolsTool : IAgentTool
{
    private static readonly RuleToolRouter Router = new();

    private IToolRegistry? _registry;

    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "find_tools",
        Description =
            "Returns the full definition (name, description, argument schema) of tools that match " +
            "a query. Use it when the tool you need is not included in the current request, then " +
            "call the returned tool by name. The universal catalog is small: prefer one search.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        Category = "Meta",
        InputSchemaJson = @"{""type"":""object"",""properties"":{""query"":{""type"":""string"",""minLength"":1},""limit"":{""type"":""integer"",""minimum"":1,""maximum"":10,""default"":5}},""required"":[""query""],""additionalProperties"":false}"
    };

    internal void Attach(IToolRegistry registry) => _registry = registry;

    public Task<ToolResult> ExecuteAsync(
        AgentToolContext context,
        JsonElement args,
        CancellationToken ct)
    {
        var query = GetString(args, "query");
        if (string.IsNullOrWhiteSpace(query))
            return Task.FromResult(ToolResult.Fail("invalid_arguments", "query is required."));

        var registry = _registry;
        if (registry is null)
            return Task.FromResult(ToolResult.Fail("tool_failed", "The tool catalog is unavailable."));

        var limit = GetInt(args, "limit", 5);
        var all = registry.GetDefinitions()
            .Where(definition => !string.Equals(
                definition.Name, Definition.Name, StringComparison.Ordinal))
            .ToList();
        var decision = Router.Route(query!, all, Math.Max(1, Math.Min(limit, 10)));

        var payload = new
        {
            total = decision.Tools.Count,
            fallback = decision.Fallback,
            tools = decision.Tools.Select(definition => new
            {
                name = definition.Name,
                description = definition.Description,
                risk = definition.Risk.ToString(),
                supportsDryRun = definition.SupportsDryRun,
                inputSchema = ParseSchema(definition.InputSchemaJson)
            }).ToList()
        };

        try
        {
            return Task.FromResult(ToolResult.Ok(
                JsonSerializer.SerializeToElement(payload),
                "Returned " + decision.Tools.Count + " matching tool definition(s)."));
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                "find_tools failed to serialize definitions: " + ex.Message +
                " [Action: check the tool schema of the matching tools]");
            return Task.FromResult(ToolResult.Fail("tool_failed", ex.Message));
        }
    }

    private static JsonElement ParseSchema(string schemaJson)
    {
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(schemaJson) ? "{}" : schemaJson);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new { });
        }
    }

    private static string? GetString(JsonElement args, string property)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }

    private static int GetInt(JsonElement args, string property, int fallback)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed))
        {
            return parsed;
        }

        return fallback;
    }
}
