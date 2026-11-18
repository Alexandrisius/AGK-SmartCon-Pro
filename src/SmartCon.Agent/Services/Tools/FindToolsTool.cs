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
        InputSchemaJson = @"{""type"":""object"",""properties"":{""query"":{""type"":""string"",""minLength"":1},""context"":{""type"":""string""},""limit"":{""type"":""integer"",""minimum"":1,""maximum"":10,""default"":8}},""required"":[""query""],""additionalProperties"":false}"
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

        var limit = System.Math.Max(1, System.Math.Min(GetInt(args, "limit", 8), 10));
        var all = registry.GetDefinitions()
            .Where(definition => !string.Equals(
                definition.Name, Definition.Name, StringComparison.Ordinal))
            .ToList();

        var state = ComposeState(query!, GetString(args, "context"));

        // The generic router always keeps the core read tools, so a small
        // find_tools limit used to be spent entirely on them and write tools were
        // never returned (live dogfood P0-3). Rank lexically first, then merge the
        // configured (possibly decision-model) router result.
        var lexical = RuleToolRouter.RankOnly(state, all, System.Math.Max(limit, 8));
        var decision = await _router
            .RouteAsync(state, all, System.Math.Max(limit, 8), ct)
            .ConfigureAwait(false);

        var merged = new List<AgentToolDefinition>(limit + 2);
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Add(AgentToolDefinition definition)
        {
            if (names.Add(definition.Name))
                merged.Add(definition);
        }

        var writeIntent = HasWriteIntent(state);
        if (writeIntent)
        {
            foreach (var definition in lexical.Tools.Where(tool => tool.Risk != AgentToolRisk.Read))
                Add(definition);
        }

        foreach (var definition in lexical.Tools)
            Add(definition);

        foreach (var definition in decision.Tools)
            Add(definition);

        var warning = (string?)null;
        if (writeIntent && merged.All(tool => tool.Risk == AgentToolRisk.Read))
        {
            // Last resort: the lexical score found no write tool at all. Return the
            // closest write tools explicitly instead of silently answering with a
            // read-only catalog.
            var writeTools = all.Where(tool => tool.Risk != AgentToolRisk.Read).ToList();
            var rescue = RuleToolRouter.RankOnly(
                state + " rename apply set hide show copy replace configure", writeTools, 3);
            foreach (var definition in rescue.Tools)
            {
                if (definition.Risk != AgentToolRisk.Read)
                    Add(definition);
            }

            warning = "No write tool matched the query directly; the closest write tools are " +
                      "included. Check their descriptions before concluding that a change is " +
                      "impossible.";
        }

        var returned = merged.Take(System.Math.Min(limit + 2, 10)).ToList();
        var payload = new
        {
            total = returned.Count,
            fallback = decision.Fallback,
            writeIntent,
            warning,
            tools = returned.Select(definition => new
            {
                name = definition.Name,
                description = definition.Description,
                risk = definition.Risk.ToString(),
                supportsDryRun = definition.SupportsDryRun,
                alreadyAvailable = context.ActiveToolNames.Contains(definition.Name),
                inputSchema = ParseSchema(definition.InputSchemaJson)
            }).ToList()
        };

        try
        {
            return ToolResult.Ok(
                JsonSerializer.SerializeToElement(payload),
                "Returned " + returned.Count + " matching tool definition(s)." +
                (warning is null ? string.Empty : " " + warning));
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                "find_tools failed to serialize definitions: " + ex.Message +
                " [Action: check the tool schema of the matching tools]");
            return ToolResult.Fail("tool_failed", ex.Message);
        }
    }

    private static bool HasWriteIntent(string text)
    {
        var value = text.ToLowerInvariant();
        string[] markers =
        {
            "переимен", "примен", "настро", "сконфигур", "скрыть", "показать", "задать",
            "установ", "замен", "скопир", "перенес", "удал", "созда", "назнач",
            "rename", "apply", "set ", "hide", "show", "copy", "replace", "configure",
            "delete", "create", "remap", "assign"
        };

        foreach (var marker in markers)
        {
#if NET8_0_OR_GREATER
            if (value.Contains(marker, StringComparison.Ordinal))
                return true;
#else
            if (value.IndexOf(marker, StringComparison.Ordinal) >= 0)
                return true;
#endif
        }

        return false;
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
