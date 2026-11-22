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
        "request the schema of the chosen tool via find_tools(query=...) or load_tool_pack; " +
        "page with offset/nextOffset when truncated=true";

    private const string StartJobName = "start_job";

    /// <summary>
    /// Bulk wording must pull the job engine into the shortlist even when the
    /// decision router prefers a single-family/single-element tool: the external
    /// run asked for "bulk job replace shared parameter across many families"
    /// and got map_family_parameters instead of start_job (S.10).
    /// </summary>
    private static readonly string[] BulkIntentMarkers =
    {
        "bulk", "batch", "mass", "migration", "migrate", "many families", "many elements",
        "hundreds", "сотни", "массов", "пакетн", "миграц", "во всех", "всех семейств"
    };

    private readonly IToolRouter _router;

    private IToolRegistry? _registry;

    public FindToolsTool(IToolRouter router)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        Definition = BuildDefinition();
    }

    public AgentToolDefinition Definition { get; }

    internal void Attach(IToolRegistry registry) => _registry = registry;

    public async Task<ToolResult> ExecuteAsync(
        AgentToolContext context,
        JsonElement args,
        CancellationToken ct)
    {
        var query = GetString(args, "query");

        if (string.IsNullOrWhiteSpace(query))
            return ExecuteListing(args, context);

        var limit = System.Math.Max(1, System.Math.Min(GetInt(args, "limit", 5), 10));
        return await ExecuteSearchAsync(
                context, query!, GetString(args, "context"), GetString(args, "pack"),
                GetStringArray(args, "fields"), limit, ct)
            .ConfigureAwait(false);
    }

    private async Task<ToolResult> ExecuteSearchAsync(
        AgentToolContext context,
        string query,
        string? contextText,
        string? packName,
        IReadOnlyList<string> fields,
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
                    string.Join(", ", AgentToolPacks.Populated.Select(candidate => candidate.Name)) + ".");
            }

            var packTools = new HashSet<string>(
                AgentToolPacks.ToolsInPack(packName!), StringComparer.Ordinal);
            candidates = all.Where(definition => packTools.Contains(definition.Name)).ToList();
        }

        var rankable = candidates
            .Where(definition => !AgentToolPacks.IsAlwaysAvailable(definition.Name))
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
                if (!AgentToolPacks.IsAlwaysAvailable(definition.Name))
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

        if (string.IsNullOrWhiteSpace(packName) && HasBulkIntent(state))
        {
            var startJob = rankable.FirstOrDefault(definition =>
                string.Equals(definition.Name, StartJobName, StringComparison.Ordinal));
            if (startJob is not null)
                Add(startJob);
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
            fields = fields.Count == 0 ? null : fields,
            hint = CatalogHint,
            tools = Cards(returned, fields, context, listing: false)
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

    private ToolResult ExecuteListing(JsonElement args, AgentToolContext context)
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
                    string.Join(", ", AgentToolPacks.Populated.Select(candidate => candidate.Name)) + ".");
            }

            var packTools = new HashSet<string>(
                AgentToolPacks.ToolsInPack(packName!), StringComparer.Ordinal);
            all = all.Where(definition => packTools.Contains(definition.Name)).ToList();
        }

        var total = all.Count;
        var offset = System.Math.Max(0, GetInt(args, "offset", 0));
        var limit = System.Math.Max(1, System.Math.Min(GetInt(args, "limit", 50), 100));
        var page = all.Skip(offset).Take(limit).ToList();
        int? nextOffset = offset + page.Count < total ? offset + page.Count : null;
        var fields = GetStringArray(args, "fields");
        var payload = new
        {
            mode = "listing",
            total,
            returned = page.Count,
            offset,
            limit,
            nextOffset,
            truncated = nextOffset is not null,
            packs = AgentToolPacks.Populated.Select(pack => pack.Name).ToList(),
            toolsPacks = PacksOf(page),
            fields = fields.Count == 0 ? null : fields,
            hint = ListingHint,
            tools = Cards(page, fields, context, listing: true)
        };

        try
        {
            return ToolResult.Ok(
                JsonSerializer.SerializeToElement(payload),
                "Listed " + page.Count + " of " + total + " tool(s) without schemas.");
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

    private static AgentToolDefinition BuildDefinition()
    {
        const string schema = @"{""type"":""object"",""properties"":{" +
            @"""query"":{""type"":""string"",""description"":""Empty lists the whole catalog or one pack without schemas""}," +
            @"""pack"":{""type"":""string""}," +
            @"""context"":{""type"":""string""}," +
            @"""limit"":{""type"":""integer"",""minimum"":1,""maximum"":100,""default"":50," +
            @"""description"":""Query mode returns at most 10 cards; listing mode pages the catalog""}," +
            @"""offset"":{""type"":""integer"",""minimum"":0,""default"":0," +
            @"""description"":""Listing mode offset; use nextOffset from the previous page""}," +
            @"""fields"":{""type"":""array"",""items"":{""type"":""string""," +
            @"""enum"":[""name"",""description"",""pack"",""packHasSkill"",""risk"",""purpose""," +
            @"""batchAnalogue"",""whenToUse""," +
            @"""whenNotToUse"",""examples"",""synonyms"",""supportsDryRun"",""timeoutMs""," +
            @"""alreadyAvailable"",""inputSchema""]}}}," +
            @"""additionalProperties"":false}";

        return new AgentToolDefinition
        {
            Name = "find_tools",
            Description =
                "Searches the full tool catalog by the user's intent and returns matching tool " +
                "cards with argument schemas. Use it when the tool you need is not in the " +
                "current request. Discovery only: it does not change the tool set. After it, " +
                "call load_tool_pack(pack=...) or load_tool_pack(tools=[...]) to enable the " +
                "chosen tools for the next round; pack= also brings the pack's skill (markdown " +
                "operating manual), tools=[...] brings a manual only for write tools. A card " +
                "with packHasSkill=true means its pack has such a manual — load the pack to read " +
                "it. A search returns compact cards (name, pack, " +
                "packHasSkill, risk, purpose, batchAnalogue, alreadyAvailable, inputSchema); " +
                "batchAnalogue names the start_job(action:...) batch path and appears only on " +
                "per-element write tools that have one; pass fields to add " +
                "whenToUse, examples, synonyms or timeoutMs. An empty query returns the whole " +
                "catalog (name, description, pack, risk, batchAnalogue) without schemas, paged with " +
                "offset/nextOffset; add pack to browse one pack. fields=[...] projects each card " +
                "onto the listed properties (for example fields=[\"name\",\"pack\"]).",
            Risk = AgentToolRisk.Read,
            SupportsDryRun = false,
            Category = "Meta",
            InputSchemaJson = schema
        };
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

    private static bool HasBulkIntent(string state)
    {
        foreach (var marker in BulkIntentMarkers)
        {
            if (state.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
    }

    private static List<Dictionary<string, object?>> Cards(
        IEnumerable<AgentToolDefinition> definitions,
        IReadOnlyList<string> fields,
        AgentToolContext context,
        bool listing)
    {
        // Search mode used to return the whole routing card (purpose, whenToUse,
        // whenNotToUse, examples, synonyms) for every hit. Five hits cost several
        // thousand tokens while the caller only needs the name, what the tool does
        // and its schema; the rest is one fields=[...] away.
        IReadOnlyList<string> selected = fields.Count > 0
            ? fields
            : listing
                ? new[] { "name", "description", "pack", "risk", "batchAnalogue" }
                : new[]
                {
                    "name", "pack", "packHasSkill", "risk", "purpose", "batchAnalogue",
                    "alreadyAvailable", "inputSchema"
                };
        var result = new List<Dictionary<string, object?>>();

        foreach (var definition in definitions)
        {
            var packName = !string.IsNullOrWhiteSpace(definition.Pack)
                           ? definition.Pack
                           : AgentToolPacks.PackOfTool(definition.Name)?.Name;
            var hasSkill = !string.IsNullOrEmpty(packName)
                           && AgentToolPackSkills.TryGet(packName!, out _);
            var card = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = definition.Name,
                ["description"] = definition.Description,
                ["risk"] = definition.Risk.ToString(),
                ["supportsDryRun"] = definition.SupportsDryRun,
                ["pack"] = packName,
                ["packHasSkill"] = hasSkill,
                ["purpose"] = definition.Purpose,
                ["whenToUse"] = definition.WhenToUse,
                ["whenNotToUse"] = definition.WhenNotToUse,
                ["examples"] = definition.Examples,
                ["synonyms"] = definition.Synonyms,
                ["timeoutMs"] = definition.TimeoutMs,
                ["alreadyAvailable"] = context.ActiveToolNames.Contains(definition.Name),
                ["inputSchema"] = ParseSchema(definition.InputSchemaJson)
            };
            if (definition.BatchAnalogue is not null)
                card["batchAnalogue"] = definition.BatchAnalogue;

            var projected = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in selected)
            {
                if (card.TryGetValue(field, out var value))
                    projected[field] = value;
            }

            result.Add(projected);
        }

        return result;
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
