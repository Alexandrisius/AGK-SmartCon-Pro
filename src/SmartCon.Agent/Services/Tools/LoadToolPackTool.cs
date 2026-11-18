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

    private const int MaxSuggestions = 3;
    private const string Hint =
        "The tool set changes at the start of the next round; the current round keeps its tools.";

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
            "its tool set. Use it after find_tools to make the discovered tools callable.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        Category = "Meta",
        InputSchemaJson =
            @"{""type"":""object"",""properties"":{" +
            @"""pack"":{""type"":""string""}," +
            @"""packs"":{""type"":""array"",""items"":{""type"":""string""}}," +
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

        var wanted = new List<string>();
        foreach (var name in packNames.Distinct(StringComparer.Ordinal))
        {
            if (!AgentToolPacks.TryGet(name, out var pack) || pack is null)
            {
                return Task.FromResult(ToolResult.Fail(
                    "invalid_arguments",
                    "Unknown pack '" + name + "'. Known packs: " +
                    string.Join(", ", AgentToolPacks.All.Select(candidate => candidate.Name)) + "."));
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

        var loaded = new List<string>();
        var alreadyLoaded = new List<string>();
        foreach (var name in wanted.Distinct(StringComparer.Ordinal))
        {
            if (_sessionContext.LoadedToolNames.Contains(name)
                || _sessionContext.PendingToolNames.Contains(name))
            {
                alreadyLoaded.Add(name);
                continue;
            }

            _sessionContext.LoadedToolNames.Add(name);
            _sessionContext.PendingToolNames.Add(name);
            loaded.Add(name);
        }

        var appliedTo = loaded.Count > 0 ? "next_round" : "none";
        var result = new
        {
            loaded,
            alreadyLoaded,
            pack = packNames.Count == 0 ? null : string.Join(",", packNames),
            totalTools = _sessionContext.LoadedToolNames.Count,
            appliedTo,
            hint = appliedTo == "none"
                ? "All requested tools are already loaded. " + Hint
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
                : "All requested tools are already loaded."));
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
