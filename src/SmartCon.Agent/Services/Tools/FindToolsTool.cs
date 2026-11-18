using System.Text.Json;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services.Tools;

/// <summary>
/// Meta-tool that returns full definitions for tools matching a query. Used when
/// the LLM works with a routed shortlist and needs a tool outside it. The query
/// goes through the configured <see cref="IToolRouter"/> — including the
/// decision-model (JEV/pplx) router when enabled — so the agent can search the
/// catalog once it understands the concrete sub-task.
/// </summary>
public sealed class FindToolsTool : IAgentTool
{
    private readonly IToolRouter _router;

    private IToolRegistry? _registry;

    public FindToolsTool(IToolRouter router)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
    }

    public AgentToolDefinition Definition { get; } = new()
    {
        Name = "find_tools",
        Description =
            "Returns the full definition (name, description, argument schema) of tools that match " +
            "a query. Use it when the tool you need is not in the current request. For an abstract " +
            "or multi-part task: read the model first, then pass a concrete query plus context " +
            "(what you learned and what you are about to change) so the catalog search returns " +
            "the right tools. Then call the returned tool by name.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        Category = "Meta",
        InputSchemaJson = @"{""type"":""object"",""properties"":{""query"":{""type"":""string"",""minLength"":1},""context"":{""type"":""string""},""limit"":{""type"":""integer"",""minimum"":1,""maximum"":10,""default"":5}},""required"":[""query""],""additionalProperties"":false}"
    };

    internal void Attach(IToolRegistry registry) => _registry = registry;

    public async Task<ToolResult> ExecuteAsync(
        AgentToolContext context,
        JsonElement args,
        CancellationToken ct)
    {
        var query = GetString(args, "query");
        if (string.IsNullOrWhiteSpace(query))
            return ToolResult.Fail("invalid_arguments", "query is required.");

        var registry = _registry;
        if (registry is null)
            return ToolResult.Fail("tool_failed", "The tool catalog is unavailable.");

        var limit = GetInt(args, "limit", 5);
        var all = registry.GetDefinitions()
            .Where(definition => !string.Equals(
                definition.Name, Definition.Name, StringComparison.Ordinal))
            .ToList();

        var state = ComposeState(query!, GetString(args, "context"));
        var decision = await _router
            .RouteAsync(state, all, System.Math.Max(1, System.Math.Min(limit, 10)), ct)
            .ConfigureAwait(false);

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
            return ToolResult.Ok(
                JsonSerializer.SerializeToElement(payload),
                "Returned " + decision.Tools.Count + " matching tool definition(s).");
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                "find_tools failed to serialize definitions: " + ex.Message +
                " [Action: check the tool schema of the matching tools]");
            return ToolResult.Fail("tool_failed", ex.Message);
        }
    }

    private static string ComposeState(string query, string? context)
    {
        if (string.IsNullOrWhiteSpace(context))
            return query;

        var trimmed = context!.Trim();
        const int maxContext = 1500;
        if (trimmed.Length > maxContext)
            trimmed = trimmed.Substring(0, maxContext);

        return query + "\n\nContext:\n" + trimmed;
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
