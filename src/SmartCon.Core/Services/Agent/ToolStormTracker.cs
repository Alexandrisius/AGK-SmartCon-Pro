using System.Text;
using System.Text.Json;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// Soft hints against call storms (C7-06, C2-06, C9-07, C1-01). The loop guard
/// only sees one exact call repeated; a storm usually varies the arguments —
/// the same tool failing with the same error code, the same page read again,
/// or the objects of one job fed to a batch tool one per call. The tracker
/// names the pattern in a warning on the result. It never blocks or stops
/// anything: the owner's rule is that only Stop and the loop guard end a turn.
/// Per turn, like <see cref="LoopGuard"/>.
/// </summary>
public sealed partial class ToolStormTracker
{
    public const int SameErrorThreshold = 3;
    public const int SameResultThreshold = 3;

    /// <summary>Host timeouts in a row after which a result carries the host hint (C1-01).</summary>
    public const int HostTimeoutHintAfter = 2;

    private const string InvalidArgumentsCode = "invalid_arguments";

    /// <summary>How the job tools mark a result for work that has not finished yet.</summary>
    private const string InProgressFlag = "mustVerify";

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
    private int _hostTimeoutStreak;
    private int _hostTimeoutSinceRound;

    // Rounds up to this one are gone from the model's side after a compaction.
    private int _forgottenThroughRound = -1;
    private int _lastRound = -1;

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

        if (round > _lastRound)
            _lastRound = round;

        if (_errorRounds.Count + _identicalCalls.Count + _readRounds.Count > MaxEntries)
        {
            _errorRounds.Clear();
            _identicalCalls.Clear();
            _readRounds.Clear();
        }

        var hints = new List<string>(3);
        var code = result.Success ? null : result.ErrorCode ?? "failed";

        // A call still inside the host says nothing about its arguments; what
        // counts is how many in a row the host left unanswered.
        if (IsHostTimeout(result))
        {
            if (_hostTimeoutStreak == 0)
                _hostTimeoutSinceRound = round;

            _hostTimeoutStreak++;
            if (_hostTimeoutStreak >= HostTimeoutHintAfter)
                hints.Add(HostTimeoutHint(_hostTimeoutStreak, _hostTimeoutSinceRound));
        }
        else
        {
            _hostTimeoutStreak = 0;
        }

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

        ObserveItemByItem(registry, toolName, argumentsJson, round, hints);
        return hints;
    }

    /// <summary>
    /// The model no longer sees what it read before a compaction, so an earlier
    /// page or an earlier result is not "already in the conversation": reading
    /// it again is a fresh read.
    /// </summary>
    public void ForgetResults()
    {
        // The error and single-object series stay: a storm is a storm whether or
        // not the model still sees its earlier calls. Only their wording changes
        // — see Where.
        _forgottenThroughRound = _lastRound;
        _identicalCalls.Clear();
        _readRounds.Clear();
    }

    private static bool IsHostTimeout(ToolResult result) =>
        !result.Success
        && string.Equals(result.ErrorCode, "tool_timeout", StringComparison.Ordinal)
        && IsFlagged(result.Data, "stillRunning");

    private static string HostTimeoutHint(int streak, int sinceRound) =>
        streak + " tool calls in a row timed out while still running in the Revit host (since round " +
        sinceRound + "): the host is not answering, and another call would only queue behind the " +
        "stuck one. Do not send more tool calls — tell the user what is blocked.";

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

    private static bool IsFlagged(JsonElement? data, string property) =>
        data.HasValue
        && data.Value.ValueKind == JsonValueKind.Object
        && data.Value.TryGetProperty(property, out var flag)
        && flag.ValueKind == JsonValueKind.True;

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
            // Work that is still in progress is the one case where the result is
            // MEANT to be the same until it finishes: the advice is to wait, not
            // to change a call that is correct.
            var inProgress = result.Success && IsFlagged(result.Data, InProgressFlag);
            hints.Add(
                inProgress
                    ? "This call was already made in round(s) " + Join(call.Rounds) + " and the work is " +
                      "still in progress: the result has not changed. Do not poll in a tight loop — " +
                      "wait longer (a larger waitSeconds) or do something else meanwhile."
                    : "The same call already returned this result in round(s) " + Join(call.Rounds) +
                      " (the arguments are identical to those rounds; if you meant to change them, " +
                      "the change did not reach the call); change the arguments or choose another path.");
        }

        Remember(call.Rounds, round);
    }

    private string SameErrorHint(string code, List<int> rounds)
    {
        var where = Where(rounds);
        if (string.Equals(code, InvalidArgumentsCode, StringComparison.Ordinal))
        {
            return "This call already failed " + where + " with invalid arguments. " +
                   "The arguments changed since then and still break the schema: " +
                   "read the listed paths against the tool schema and rebuild the call from it, " +
                   "or choose another path.";
        }

        // A reason the model can no longer see cannot be read once more.
        var readAgain = rounds.Any(round => round > _forgottenThroughRound)
            ? "Read the reason once more and "
            : string.Empty;
        return "This call already failed " + where + " with the same error (" + code +
               "). " + readAgain + "choose another path: another tool, or tell the user " +
               "what blocks it.";
    }

    /// <summary>
    /// Where the earlier calls of a series happened. A compaction removed every
    /// round up to <see cref="_forgottenThroughRound"/> from the model's side, so
    /// those rounds are counted, not named: the model cannot look them up.
    /// </summary>
    private string Where(IEnumerable<int> rounds)
    {
        var seen = new List<int>();
        var hidden = 0;
        foreach (var round in rounds.Distinct())
        {
            if (round <= _forgottenThroughRound)
                hidden++;
            else
                seen.Add(round);
        }

        if (hidden == 0)
            return "in round(s) " + Join(seen);

        var earlier = hidden + " time(s) before the last compaction";
        return seen.Count == 0 ? earlier : "in round(s) " + Join(seen) + " and " + earlier;
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

}
