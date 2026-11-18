using System.Text.Json;
using SmartCon.Agent.Services.Llm;
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
    private const string CatalogHint =
        "call load_tool_pack(pack=...) or load_tool_pack(tools=[...]) to enable; " +
        "find_tools does not change the tool set";

    private const string ListingHint =
        "request the schema of the chosen tool via find_tools(query=...) or load_tool_pack";

    private readonly IToolRouter _router;
    private readonly bool _catalogV2;

    private IToolRegistry? _registry;

    public FindToolsTool(IToolRouter router, AgentLlmSettings? settings = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _catalogV2 = settings?.ToolCatalogV2 == true;
        Definition = BuildDefinition(_catalogV2);
    }

    public AgentToolDefinition Definition { get; }

    internal void Attach(IToolRegistry registry) => _registry = registry;

    public async Task<ToolResult> ExecuteAsync(
        AgentToolContext context,
        JsonElement args,
        CancellationToken ct)
    {
        var query = GetString(args, "query");
        var limit = System.Math.Max(1, System.Math.Min(GetInt(args, "limit", _catalogV2 ? 5 : 8), 10));

        if (_catalogV2)
        {
            var fields = GetStringArray(args, "fields");
            if (fields.Count > 0 && string.IsNullOrWhiteSpace(query))
                return ExecuteListing(args, limit);

            if (string.IsNullOrWhiteSpace(query))
                return ToolResult.Fail("invalid_arguments", "query is required.");

            return await ExecuteCatalogV2Async(
                    context, query!, GetString(args, "context"), GetString(args, "pack"), limit, ct)
                .ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(query))
            return ToolResult.Fail("invalid_arguments", "query is required.");

        return await ExecuteLegacyAsync(
                context, query!, GetString(args, "context"), limit, ct)
            .ConfigureAwait(false);
    }

    private async Task<ToolResult> ExecuteLegacyAsync(
        AgentToolContext context,
        string query,
        string? contextText,
        int limit,
        CancellationToken ct)
    {
        var registry = _registry;
        if (registry is null)
            return ToolResult.Fail("tool_failed", "The tool catalog is unavailable.");

        var all = registry.GetDefinitions()
            .Where(definition => !string.Equals(
                definition.Name, Definition.Name, StringComparison.Ordinal))
            .ToList();

        var state = ComposeState(query, contextText);

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

    private async Task<ToolResult> ExecuteCatalogV2Async(
        AgentToolContext context,
        string query,
        string? contextText,
        string? packName,
        int limit,
        CancellationToken ct)
    {
        var registry = _registry;
        if (registry is null)
            return ToolResult.Fail("tool_failed", "The tool catalog is unavailable.");

        var all = registry.GetDefinitions()
            .Where(definition => !string.Equals(
                definition.Name, Definition.Name, StringComparison.Ordinal))
            .Where(definition => !string.Equals(
                definition.Name, LoadToolPackTool.ToolName, StringComparison.Ordinal))
            .ToList();

        var candidates = all;
        if (!string.IsNullOrWhiteSpace(packName))
        {
            if (!AgentToolPacks.TryGet(packName!, out _))
            {
                return ToolResult.Fail(
                    "invalid_arguments",
                    "Unknown pack '" + packName + "'. Known packs: " +
                    string.Join(", ", AgentToolPacks.All.Select(candidate => candidate.Name)) + ".");
            }

            var packTools = new HashSet<string>(
                AgentToolPacks.ToolsInPack(packName!), StringComparer.Ordinal);
            candidates = all.Where(definition => packTools.Contains(definition.Name)).ToList();
        }

        var rankable = candidates
            .Where(definition => !AgentToolPacks.IsCore(definition.Name))
            .ToList();
        var state = ComposeState(query, contextText);

        var lexical = ToolCardRouter.TopCandidates(state, rankable, 50);

        var chosen = new List<AgentToolDefinition>(6);
        if (rankable.Count > 0)
        {
            var decision = await _router
                .RouteAsync(state, rankable, 6, ct)
                .ConfigureAwait(false);
            foreach (var definition in decision.Tools)
            {
                if (!AgentToolPacks.IsCore(definition.Name))
                    chosen.Add(definition);
            }
        }

        var merged = new List<AgentToolDefinition>(limit + chosen.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Add(AgentToolDefinition definition)
        {
            if (names.Add(definition.Name))
                merged.Add(definition);
        }

        foreach (var definition in chosen)
            Add(definition);

        foreach (var definition in lexical)
            Add(definition);

        var returned = merged.Take(limit).ToList();
        var payload = new
        {
            total = returned.Count,
            packs = PacksOf(returned),
            hint = CatalogHint,
            tools = returned.Select(definition => new
            {
                name = definition.Name,
                description = definition.Description,
                risk = definition.Risk.ToString(),
                supportsDryRun = definition.SupportsDryRun,
                pack = definition.Pack,
                purpose = definition.Purpose,
                whenToUse = definition.WhenToUse,
                whenNotToUse = definition.WhenNotToUse,
                examples = definition.Examples,
                synonyms = definition.Synonyms,
                timeoutMs = definition.TimeoutMs,
                alreadyAvailable = context.ActiveToolNames.Contains(definition.Name),
                inputSchema = ParseSchema(definition.InputSchemaJson)
            }).ToList()
        };

        try
        {
            return ToolResult.Ok(
                JsonSerializer.SerializeToElement(payload),
                "Returned " + returned.Count + " matching tool definition(s).");
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                "find_tools failed to serialize definitions: " + ex.Message +
                " [Action: check the tool schema of the matching tools]");
            return ToolResult.Fail("tool_failed", ex.Message);
        }
    }

    private ToolResult ExecuteListing(JsonElement args, int limit)
    {
        var registry = _registry;
        if (registry is null)
            return ToolResult.Fail("tool_failed", "The tool catalog is unavailable.");

        var all = registry.GetDefinitions()
            .Where(definition => !string.Equals(
                definition.Name, Definition.Name, StringComparison.Ordinal))
            .Where(definition => !string.Equals(
                definition.Name, LoadToolPackTool.ToolName, StringComparison.Ordinal))
            .ToList();

        var packName = GetString(args, "pack");
        if (!string.IsNullOrWhiteSpace(packName))
        {
            if (!AgentToolPacks.TryGet(packName!, out _))
            {
                return ToolResult.Fail(
                    "invalid_arguments",
                    "Unknown pack '" + packName + "'. Known packs: " +
                    string.Join(", ", AgentToolPacks.All.Select(candidate => candidate.Name)) + ".");
            }

            var packTools = new HashSet<string>(
                AgentToolPacks.ToolsInPack(packName!), StringComparer.Ordinal);
            all = all.Where(definition => packTools.Contains(definition.Name)).ToList();
        }

        var returned = all.Take(limit).ToList();
        var payload = new
        {
            mode = "listing",
            total = returned.Count,
            packs = PacksOf(returned),
            hint = ListingHint,
            tools = returned.Select(definition => new
            {
                name = definition.Name,
                description = definition.Description,
                pack = definition.Pack,
                risk = definition.Risk.ToString()
            }).ToList()
        };

        try
        {
            return ToolResult.Ok(
                JsonSerializer.SerializeToElement(payload),
                "Listed " + returned.Count + " tool(s) without schemas.");
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                "find_tools failed to serialize the tool listing: " + ex.Message +
                " [Action: check the tool catalog]");
            return ToolResult.Fail("tool_failed", ex.Message);
        }
    }

    private static List<string> PacksOf(IEnumerable<AgentToolDefinition> definitions) =>
        definitions
            .Select(definition => definition.Pack)
            .Where(pack => !string.IsNullOrWhiteSpace(pack))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(pack => pack, StringComparer.Ordinal)
            .ToList();

    private static AgentToolDefinition BuildDefinition(bool catalogV2)
    {
        var schema = catalogV2
            ? @"{""type"":""object"",""properties"":{" +
              @"""query"":{""type"":""string"",""minLength"":1}," +
              @"""pack"":{""type"":""string""}," +
              @"""context"":{""type"":""string""}," +
              @"""limit"":{""type"":""integer"",""minimum"":1,""maximum"":10,""default"":5}," +
              @"""fields"":{""type"":""array"",""items"":{""type"":""string""," +
              @"""enum"":[""name"",""description""]}}}," +
              @"""additionalProperties"":false}"
            : @"{""type"":""object"",""properties"":{""query"":{""type"":""string"",""minLength"":1},""context"":{""type"":""string""},""limit"":{""type"":""integer"",""minimum"":1,""maximum"":10,""default"":8}},""required"":[""query""],""additionalProperties"":false}";

        return new AgentToolDefinition
        {
            Name = "find_tools",
            Description = catalogV2
                ? "Searches the full tool catalog by the user's intent and returns matching tool " +
                  "cards with argument schemas. Use it when the tool you need is not in the " +
                  "current request. Discovery only: it does not change the tool set. After it, " +
                  "call load_tool_pack(pack=...) or load_tool_pack(tools=[...]) to enable the " +
                  "chosen tools for the next round. Pass fields=[\"name\",\"description\"] with " +
                  "an empty query to browse the catalog or one pack compactly without schemas."
                : "Returns the full definition (name, description, argument schema) of tools that match " +
                  "a query. Use it when the tool you need is not in the current request. For an abstract " +
                  "or multi-part task: read the model first, then pass a concrete query plus context " +
                  "(what you learned and what you are about to change) so the catalog search returns " +
                  "the right tools. Then call the returned tool by name.",
            Risk = AgentToolRisk.Read,
            SupportsDryRun = false,
            Category = "Meta",
            InputSchemaJson = schema
        };
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

    private static List<string> GetStringArray(JsonElement args, string property)
    {
        var result = new List<string>();
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(item.GetString()))
            {
                result.Add(item.GetString()!);
            }
        }

        return result;
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
