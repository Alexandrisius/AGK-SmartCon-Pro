using System.Text.Json;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services.Tools;

/// <summary>
/// Meta-tool of the v2 catalog: enables a whole pack or explicit tools for the
/// next round. It never executes the loaded tools itself and the current round
/// keeps the tool set it started with (runbook §7.2).
/// </summary>
public sealed class LoadToolPackTool : IAgentTool
{
    public const string ToolName = "load_tool_pack";

    /// <summary>
    /// Upper bound of packs per call (#453): every pack adds a markdown skill to
    /// the result, so an unbounded list could push the largest call past the meta
    /// budget no matter how high it is. The rest is loadable in a second call.
    /// </summary>
    public const int MaxPacksPerCall = 5;

    private const int MaxSuggestions = 3;
    private const int MaxSchemas = 8;
    private const string Hint =
        "The requested tools are callable from the next round; the current round keeps " +
        "its tools.";

    private readonly AgentSessionContext _sessionContext;

    private IToolRegistry? _registry;

    public LoadToolPackTool(AgentSessionContext sessionContext)
    {
        _sessionContext = sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));
    }

    public AgentToolDefinition Definition { get; } = new()
    {
        Name = ToolName,
            Description =
            "Enables a tool pack or explicit tools for the next round; the current round keeps " +
            "its tool set. Returns the input schemas of the tools it enabled and, for packs " +
            "that have one, the pack skill (markdown operating manual) — one entry per pack in " +
            "'skills', each sent once per conversation. Loading a single tool also delivers " +
            "the skill of its pack; prefer pack= to get the sibling tools too. At most " +
            MaxPacksPerCall + " packs per call: load the rest in a second call. Use it after " +
            "find_tools to make the discovered tools callable.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        Category = "Meta",
        InputSchemaJson =
            @"{""type"":""object"",""properties"":{" +
            @"""pack"":{""type"":""string""}," +
            @"""packs"":{""type"":""array"",""items"":{""type"":""string""},""maxItems"":" + MaxPacksPerCall + @"}," +
            @"""tools"":{""type"":""array"",""items"":{""type"":""string""}}," +
            @"""context"":{""type"":""string""}}," +
            @"""additionalProperties"":false}"
    };

    internal void Attach(IToolRegistry registry) => _registry = registry;

    public Task<ToolResult> ExecuteAsync(
        AgentToolContext context,
        JsonElement args,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var registry = _registry;
        if (registry is null)
            return Task.FromResult(
                ToolResult.Fail("tool_failed", "The tool catalog is unavailable."));

        var packName = GetString(args, "pack");
        var packNames = GetStringArray(args, "packs");
        if (!string.IsNullOrWhiteSpace(packName) && !packNames.Contains(packName!))
            packNames.Insert(0, packName!);

        var requested = GetStringArray(args, "tools");
        if (packNames.Count == 0 && requested.Count == 0)
        {
            return Task.FromResult(ToolResult.Fail(
                "invalid_arguments",
                "Provide 'pack', 'packs' or at least one tool name in 'tools'."));
        }

        // The schema caps 'packs' at maxItems, but a provider that ignores it must
        // still get a bounded answer (#453): every pack adds a markdown skill, so
        // the worst call is the largest five skills plus the tool schemas.
        var distinctPacks = packNames.Distinct(StringComparer.Ordinal).ToList();
        if (distinctPacks.Count > MaxPacksPerCall)
        {
            return Task.FromResult(ToolResult.Fail(
                "invalid_arguments",
                "At most " + MaxPacksPerCall + " pack(s) can be requested per call, got " +
                distinctPacks.Count + ": " +
                string.Join(", ", distinctPacks.Take(MaxPacksPerCall)) +
                ", and " + (distinctPacks.Count - MaxPacksPerCall) + " more. " +
                "Load the rest in a second call."));
        }

        var wanted = new List<string>();
        foreach (var name in distinctPacks)
        {
            if (!AgentToolPacks.TryGet(name, out var pack) || pack is null)
            {
                return Task.FromResult(ToolResult.Fail(
                    "invalid_arguments",
                    "Unknown pack '" + name + "'. Known packs: " +
                    string.Join(
                        ", ",
                        AgentToolPacks.Populated.Select(candidate => candidate.Name)) + "."));
            }

            if (pack.Tools.Count == 0)
            {
                return Task.FromResult(ToolResult.Fail(
                    "pack_reserved_empty",
                    "The pack '" + name + "' is reserved for a future version and contains no " +
                    "tools yet. Use: " +
                    string.Join(
                        ", ",
                        AgentToolPacks.Populated.Select(candidate => candidate.Name)) + "."));
            }

            wanted.AddRange(AgentToolPacks.ToolsInPack(name));
        }

        var knownNames = registry.GetDefinitions()
            .Select(definition => definition.Name)
            .ToList();
        var unknown = new List<object>();
        foreach (var name in requested)
        {
            if (!registry.TryGet(name, out _))
            {
                unknown.Add(new
                {
                    name,
                    suggestions = Suggest(name, knownNames)
                });
                continue;
            }

            wanted.Add(name);
        }

        if (unknown.Count > 0)
        {
            var payload = JsonSerializer.SerializeToElement(new { unknown });
            return Task.FromResult(ToolResult.Fail(
                "unknown_tool",
                "Unknown tool name(s) in 'tools'. Use find_tools to discover the correct names.",
                data: payload));
        }

        // A skill belongs to a pack, but the model may load a single tool by name.
        // Deliver the manual of every pack the loaded tools belong to: loading one
        // tool must not skip the knowledge of its pack (#400). The skill channel
        // is capped at MaxPacksPerCall like the pack list (#453): a tools= call
        // spanning many packs attaches at most five manuals — the tools still
        // load, only the skill delivery is bounded. Explicitly requested packs
        // keep their slots; owner packs fill the rest in name order, so the
        // choice is deterministic.
        var skillPacks = new List<string>(distinctPacks);
        var additionalOwners = new List<string>();
        foreach (var name in requested)
        {
            var owner = AgentToolPacks.PackOfTool(name);
            if (owner is null
                || skillPacks.Contains(owner.Name, StringComparer.Ordinal)
                || additionalOwners.Contains(owner.Name, StringComparer.Ordinal))
            {
                continue;
            }

            additionalOwners.Add(owner.Name);
        }

        var totalSkillPacks = skillPacks.Count + additionalOwners.Count;
        string? skillPackNote = null;
        foreach (var owner in additionalOwners.OrderBy(name => name, StringComparer.Ordinal))
        {
            if (skillPacks.Count < MaxPacksPerCall)
            {
                skillPacks.Add(owner);
                continue;
            }

            skillPackNote = "Skills attached for " + skillPacks.Count + " of " +
                totalSkillPacks + " packs; call load_tool_pack for the rest.";
            break;
        }

        // "Loaded" must mean "callable right now", not "remembered for this
        // session": a request carries only a bounded slice of the catalog, so a
        // tool that was loaded earlier can still be absent from the current
        // request. Only the tools actually present there are reported as
        // alreadyLoaded; the rest are queued and moved to the front of the
        // recency order so they survive the next selection.
        var inRequest = new HashSet<string>(
            _sessionContext.ActiveToolNames, StringComparer.Ordinal);

        var loaded = new List<string>();
        var alreadyLoaded = new List<string>();
        foreach (var name in wanted.Distinct(StringComparer.Ordinal))
        {
            if (inRequest.Contains(name))
            {
                alreadyLoaded.Add(name);
                continue;
            }

            _sessionContext.TouchTool(name);
            _sessionContext.PendingToolNames.Add(name);
            loaded.Add(name);
        }

        var appliedTo = loaded.Count > 0 ? "next_round" : "none";

        // The counters must add up: catalogTotal = coreTools + loadedTools +
        // remainingAvailable. The old payload reported totalTools (loaded) next to
        // remainingAvailable (non-core only) and the model saw 14 + 26 = 40 in a
        // 52-tool catalog (external run 6, P2).
        var definitions = registry.GetDefinitions().ToList();
        var alwaysAvailable = definitions
            .Where(definition => AgentToolPacks.IsAlwaysAvailable(definition.Name))
            .ToList();
        var availableNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in alwaysAvailable)
            availableNames.Add(definition.Name);
        availableNames.UnionWith(inRequest);
        availableNames.UnionWith(_sessionContext.LoadedToolNames);

        var availableNow = definitions.Count(definition => availableNames.Contains(definition.Name));
        var loadedTools = definitions.Count(definition =>
            _sessionContext.LoadedToolNames.Contains(definition.Name));
        var coreTools = definitions.Count(definition =>
            AgentToolPacks.IsAlwaysAvailable(definition.Name)
            && !_sessionContext.LoadedToolNames.Contains(definition.Name));
        var inRequestTools = definitions.Count(definition =>
            inRequest.Contains(definition.Name)
            && !AgentToolPacks.IsAlwaysAvailable(definition.Name)
            && !_sessionContext.LoadedToolNames.Contains(definition.Name));
        var remainingByPack = definitions
            .Where(definition => !availableNames.Contains(definition.Name))
            .GroupBy(definition => !string.IsNullOrWhiteSpace(definition.Pack)
                ? definition.Pack!
                : AgentToolPacks.PackOfTool(definition.Name)?.Name ?? "(none)")
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count());
        var remainingAvailable = definitions.Count - availableNow;

        // Schemas of the tools this call touched: after a session restart the model
        // otherwise needs find_tools again just to learn the signatures (external
        // run 6, P2). Bounded so a big pack cannot flood the context.
        var schemaNames = new HashSet<string>(loaded, StringComparer.Ordinal);
        schemaNames.UnionWith(alreadyLoaded);
        var schemas = definitions
            .Where(definition => schemaNames.Contains(definition.Name))
            .Take(MaxSchemas)
            .Select(definition => new
            {
                name = definition.Name,
                description = definition.Description,
                inputSchema = ParseSchema(definition.InputSchemaJson)
            })
            .ToList();

        // Pack skills (#400): the operating manual of every requested pack, in
        // markdown. Each skill is sent once per conversation — a repeated
        // load_tool_pack reports it as already sent instead of duplicating the
        // text in the prompt. Every pack gets its own entry: dropping the second
        // markdown of a multi-pack call while marking it sent would lose it for
        // the whole conversation.
        var skills = new List<object>();
        var skillAlreadySent = new List<string>();
        foreach (var name in skillPacks.Distinct(StringComparer.Ordinal))
        {
            if (!AgentToolPackSkills.TryGet(name, out var markdown) || markdown is null)
                continue;

            if (_sessionContext.SentPackSkills.Contains(name))
            {
                skillAlreadySent.Add(name);
                continue;
            }

            _sessionContext.SentPackSkills.Add(name);
            skills.Add(new { pack = name, markdown });
        }

        var result = new
        {
            loaded,
            alreadyLoaded,
            pack = packNames.Count == 0 ? null : string.Join(",", packNames),
            catalogTotal = definitions.Count,
            coreTools,
            loadedTools,
            inRequestTools,
            availableNow,
            remainingAvailable,
            remainingByPack,
            skills = skills.Count == 0 ? null : skills,
            skillAlreadySent = skillAlreadySent.Count == 0 ? null : skillAlreadySent,
            skillPackNote,
            // Legacy field: tools enabled in this conversation, including pack names
            // that the registry does not know (should not happen in production).
            totalTools = _sessionContext.LoadedToolNames.Count,
            counting = "catalogTotal = coreTools + loadedTools + inRequestTools + " +
                       "remainingAvailable",
            schemas,
            schemasTruncated = schemaNames.Count > schemas.Count,
            appliedTo,
            hint = appliedTo == "none"
                ? "All requested tools are already available in this request."
                : Hint
        };

        SmartConLogger.Info(
            "Agent tool pack queued for the next round: packs=" +
            (packNames.Count == 0 ? "-" : string.Join(",", packNames)) +
            ", loaded=" + loaded.Count +
            ", alreadyLoaded=" + alreadyLoaded.Count +
            ", total=" + _sessionContext.LoadedToolNames.Count);

        return Task.FromResult(ToolResult.Ok(
            JsonSerializer.SerializeToElement(result),
            loaded.Count > 0
                ? "Loaded " + loaded.Count + " tool(s) for the next round."
                : "All requested tools are already available in this request."));
    }

    private static List<string> Suggest(string name, IReadOnlyList<string> knownNames)
    {
        var normalized = name.ToLowerInvariant();
        return knownNames
            .Select(candidate => (
                Name: candidate,
                Distance: Distance(normalized, candidate.ToLowerInvariant())))
            .OrderBy(pair => pair.Distance)
            .ThenBy(pair => pair.Name, StringComparer.Ordinal)
            .Take(MaxSuggestions)
            .Select(pair => pair.Name)
            .ToList();
    }

    private static int Distance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var index = 0; index <= right.Length; index++)
            previous[index] = index;

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var cost = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = System.Math.Min(
                    System.Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + cost);
            }

            var swap = previous;
            previous = current;
            current = swap;
        }

        return previous[right.Length];
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
            if (item.ValueKind != JsonValueKind.String)
                continue;

            var name = item.GetString();
            if (!string.IsNullOrWhiteSpace(name))
                result.Add(name!);
        }

        return result;
    }
}
