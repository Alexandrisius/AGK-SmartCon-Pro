using System.Text.Json;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// Soft hints against call storms (C7-06, C2-06, C9-07, DB-09). The loop guard
/// only sees one exact call repeated; a storm usually varies the arguments —
/// the same tool failing with the same error code, the same page read again,
/// or one item per call where the tool takes a batch. The tracker names the
/// pattern in a warning on the result. It never blocks or stops anything: the
/// owner's rule is that only Stop and the loop guard end a turn. Per turn,
/// like <see cref="LoopGuard"/>.
/// </summary>
public sealed class ToolStormTracker
{
    public const int SameErrorThreshold = 3;
    public const int SameResultThreshold = 3;
    public const int SingleItemSeriesThreshold = 3;
    public const int SingleItemSeriesWindowRounds = 12;
    private const int MaxEntries = 200;
    private const int MaxRememberedRounds = 6;

    public const string ReadToolName = "read_tool_result";

    // Produced by the harness, not by the tool: they carry their own guidance.
    private static readonly HashSet<string> HarnessErrorCodes = new(StringComparer.Ordinal)
    {
        "loop_detected", "loop_aborted", "cancelled", "tool_timeout"
    };

    private readonly Dictionary<string, List<int>> _errorRounds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IdenticalCall> _identicalCalls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<int>> _readRounds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<int>> _singleItemRounds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BatchShape?> _shapes = new(StringComparer.Ordinal);

    /// <summary>
    /// Records one executed call and returns the warnings to attach to its
    /// result (empty when the call fits no storm pattern).
    /// </summary>
    public IReadOnlyList<string> Observe(
        IToolRegistry? registry,
        string toolName,
        string? argumentsJson,
        ToolResult result,
        int round)
    {
        if (string.IsNullOrEmpty(toolName) || result is null)
            return Array.Empty<string>();

        if (_errorRounds.Count + _identicalCalls.Count + _readRounds.Count > MaxEntries)
        {
            _errorRounds.Clear();
            _identicalCalls.Clear();
            _readRounds.Clear();
        }

        var hints = new List<string>(2);
        var code = result.Success ? null : result.ErrorCode ?? "failed";

        // Same tool, same error code, any arguments.
        if (code is not null && !HarnessErrorCodes.Contains(code))
        {
            var rounds = Rounds(_errorRounds, toolName + "\n" + code);
            if (rounds.Count >= SameErrorThreshold - 1)
                hints.Add(SameErrorHint(code, rounds));
            Remember(rounds, round);
        }

        if (hints.Count == 0 && !RepeatReadHint(toolName, result, round, hints))
            ObserveIdenticalCall(toolName, argumentsJson ?? string.Empty, result, round, hints);

        ObserveSeries(registry, toolName, argumentsJson, round, hints);
        return hints;
    }

    private bool RepeatReadHint(string toolName, ToolResult result, int round, List<string> hints)
    {
        if (!string.Equals(toolName, ReadToolName, StringComparison.Ordinal)
            || !result.Success
            || !result.Data.HasValue
            || result.Data.Value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var data = result.Data.Value;
        if (!data.TryGetProperty("handle", out var handle)
            || handle.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        // The result carries the EFFECTIVE offset/limit (defaults and the cap
        // applied), so two spellings of the same page share one key.
        data.TryGetProperty("path", out var path);
        data.TryGetProperty("offset", out var offsetValue);
        data.TryGetProperty("limit", out var limitValue);
        var pathText = path.ValueKind == JsonValueKind.String ? path.GetString()! : "<root>";
        var offset = Text(offsetValue);
        var limit = Text(limitValue);
        var key = handle.GetString() + "\n" + pathText + "\n" + offset + "\n" + limit;

        var rounds = Rounds(_readRounds, key);
        if (rounds.Count == 0)
        {
            rounds.Add(round);
            return false;
        }

        hints.Add(
            "This same page was already read in round(s) " + Join(rounds) + " (handle " +
            handle.GetString() + ", path " + pathText + ", offset " + offset + ", limit " + limit +
            "); its content is already in the conversation above — do not read it again.");
        Remember(rounds, round);
        return true;
    }

    private static string Text(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number || value.ValueKind == JsonValueKind.String
            ? value.ToString() ?? string.Empty
            : string.Empty;

    private void ObserveIdenticalCall(
        string toolName,
        string argumentsJson,
        ToolResult result,
        int round,
        List<string> hints)
    {
        var key = toolName + "\n" + argumentsJson.Trim();
        if (!_identicalCalls.TryGetValue(key, out var call))
        {
            call = new IdenticalCall();
            _identicalCalls[key] = call;
        }

        // The payload stands for the result through its hash (never kept in
        // memory): a poll whose counters moved is another result even when the
        // message reads the same, and a variable message must not hide a repeat.
        string fingerprint;
        if (result.Data.HasValue && result.Data.Value.ValueKind != JsonValueKind.Undefined)
        {
            fingerprint = (result.Success ? "ok|d|" : "fail|d|") + (result.ErrorCode ?? string.Empty)
                          + "|" + result.Data.Value.GetRawText().GetHashCode();
        }
        else
        {
            fingerprint = (result.Success ? "ok|m|" : "fail|m|") + (result.ErrorCode ?? string.Empty)
                          + "|" + (result.Message ?? string.Empty);
        }

        if (!string.Equals(call.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            call.Fingerprint = fingerprint;
            call.Rounds.Clear();
        }

        if (call.Rounds.Count >= SameResultThreshold - 1)
        {
            // Successful repeats count too (ORCH-12): the model re-sends the call
            // believing it changed the arguments, and the result cannot differ.
            hints.Add(
                "The same call already returned this result in round(s) " + Join(call.Rounds) +
                " (the arguments are identical to those rounds; if you meant to change them, " +
                "the change did not reach the call); change the arguments or choose another path.");
        }

        Remember(call.Rounds, round);
    }

    private void ObserveSeries(
        IToolRegistry? registry,
        string toolName,
        string? argumentsJson,
        int round,
        List<string> hints)
    {
        var shape = registry is null ? null : Shape(registry, toolName);
        if (shape is null)
            return;

        // A plan is applied by repeating the previewed call, so single-step
        // plans always come in pairs: that is the plan contract, not a storm.
        if (AgentToolPacks.SystemToolNames.Contains(toolName)
            && !string.Equals(toolName, AgentToolPacks.LoadToolPackName, StringComparison.Ordinal))
        {
            return;
        }

        var rounds = Rounds(_singleItemRounds, toolName);
        if (!IsSingleItemCall(argumentsJson, shape))
        {
            // The model batched (or the call has no batch argument at all):
            // the series is over and starts counting from scratch.
            rounds.Clear();
            return;
        }

        rounds.Add(round);
        if (rounds.Count < SingleItemSeriesThreshold
            || round - rounds[rounds.Count - SingleItemSeriesThreshold] > SingleItemSeriesWindowRounds)
        {
            return;
        }

        hints.Add(
            "Single-item calls of " + toolName + " in round(s) " +
            Join(rounds.Skip(rounds.Count - SingleItemSeriesThreshold)) +
            ": this tool takes several items in one call; batch them.");
        rounds.Clear();
    }

    private static string SameErrorHint(string code, List<int> rounds) =>
        "The same call already failed in round(s) " + Join(rounds) + " with the same error (" + code +
        "); changing arguments of this kind will not help — read the reason and choose another path.";

    private static bool IsSingleItemCall(string? argumentsJson, BatchShape shape)
    {
        // net48 ref assemblies carry no [NotNullWhen] on IsNullOrWhiteSpace.
        var json = argumentsJson ?? string.Empty;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (shape.Arrays.Contains(property.Name))
                {
                    if (property.Value.ValueKind == JsonValueKind.Array && property.Value.GetArrayLength() == 1)
                        return true;
                }
                else if (shape.Caps.Contains(property.Name)
                         && property.Value.ValueKind == JsonValueKind.Number
                         && property.Value.TryGetInt64(out var value) && value == 1)
                {
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// The batch arguments of a tool, read once from its schema: every array
    /// property, and every integer property whose default is greater than one
    /// (a batch bound such as a size or a maximum; passing 1 for it means the
    /// model chose to process one item at a time).
    /// </summary>
    private BatchShape? Shape(IToolRegistry registry, string toolName)
    {
        if (_shapes.TryGetValue(toolName, out var cached))
            return cached;

        BatchShape? shape = null;
        if (registry.TryGet(toolName, out var tool) && tool is not null)
        {
            try
            {
                var json = tool.Definition.InputSchemaJson;
                using var document = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(json) ? "{}" : json);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("properties", out var properties)
                    && properties.ValueKind == JsonValueKind.Object)
                {
                    var arrays = new List<string>();
                    var caps = new List<string>();
                    foreach (var property in properties.EnumerateObject())
                    {
                        if (property.Value.ValueKind != JsonValueKind.Object
                            || !property.Value.TryGetProperty("type", out var type)
                            || type.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        var kind = type.GetString();
                        if (string.Equals(kind, "array", StringComparison.Ordinal))
                        {
                            arrays.Add(property.Name);
                        }
                        else if (string.Equals(kind, "integer", StringComparison.Ordinal)
                                 && property.Value.TryGetProperty("default", out var fallback)
                                 && fallback.ValueKind == JsonValueKind.Number
                                 && fallback.TryGetInt64(out var defaultValue)
                                 && defaultValue > 1)
                        {
                            caps.Add(property.Name);
                        }
                    }

                    if (arrays.Count > 0)
                        shape = new BatchShape(arrays.ToArray(), caps.ToArray());
                }
            }
            catch (JsonException)
            {
                shape = null;
            }
        }

        _shapes[toolName] = shape;
        return shape;
    }

    private static List<int> Rounds(Dictionary<string, List<int>> source, string key)
    {
        if (!source.TryGetValue(key, out var rounds))
        {
            rounds = new List<int>();
            source[key] = rounds;
        }

        return rounds;
    }

    private static void Remember(List<int> rounds, int round)
    {
        rounds.Add(round);
        if (rounds.Count > MaxRememberedRounds)
            rounds.RemoveRange(0, rounds.Count - MaxRememberedRounds);
    }

    private static string Join(IEnumerable<int> rounds) =>
        string.Join(", ", rounds.Distinct().Select(value => value.ToString()));

    private sealed class IdenticalCall
    {
        public string Fingerprint { get; set; } = string.Empty;

        public List<int> Rounds { get; } = new();
    }

    private sealed class BatchShape
    {
        public BatchShape(string[] arrays, string[] caps)
        {
            Arrays = arrays;
            Caps = caps;
        }

        public string[] Arrays { get; }

        public string[] Caps { get; }
    }
}
