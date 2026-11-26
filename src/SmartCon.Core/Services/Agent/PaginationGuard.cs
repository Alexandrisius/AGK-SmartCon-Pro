using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// Canonical identity of a tool call: the tool name plus every argument with
/// the keys sorted, so two spellings of the same call share one key. Page
/// arguments (limit/offset/…) can be dropped for callers that treat every page
/// of one object as the same read.
/// </summary>
public static class ToolCallIdentity
{
    public static readonly string[] PageArgumentNames =
    {
        "limit", "offset", "page", "pageSize", "cursor", "nextOffset"
    };

    /// <summary>
    /// Canonical key, or null when the arguments are not a JSON object (nothing
    /// to identify). <paramref name="ignorePageArguments"/> builds the identity
    /// of the read TARGET instead of the identity of one page.
    /// </summary>
    public static string? Canonical(
        string toolName,
        string? argumentsJson,
        bool ignorePageArguments = false)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return null;

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(argumentsJson!.Trim()).RootElement;
        }
        catch (JsonException)
        {
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object)
            return null;

        var canonical = new JsonObject();
        var hasPageArgument = false;
        foreach (var property in root.EnumerateObject()
                     .OrderBy(property => property.Name, StringComparer.Ordinal))
        {
            if (PageArgumentNames.Contains(property.Name, StringComparer.Ordinal))
            {
                hasPageArgument = true;
                if (ignorePageArguments)
                    continue;
            }

            canonical[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }

        if (ignorePageArguments && !hasPageArgument)
            return null;

        return toolName + "\n" + canonical.ToJsonString();
    }
}

/// <summary>What the pagination guard decided about one call.</summary>
public enum PaginationGuardAction
{
    Proceed,

    Warn,

    Block
}

public sealed record PaginationVerdict(
    PaginationGuardAction Action,
    int Calls,
    string? Hint)
{
    public static PaginationVerdict Proceed { get; } =
        new(PaginationGuardAction.Proceed, 0, null);
}

/// <summary>
/// Stops paging storms on one object (S.45): the same tool aimed at the same
/// target with only page arguments varying is one read repeated. After
/// <see cref="WarnThreshold"/> pages the result carries a warning with the way
/// out (read_tool_result / one large page), after <see cref="BlockThreshold"/>
/// the call is refused before it reaches Revit. The guard lives for the whole
/// conversation: a user message between turns must not reset the count — the
/// observed storm spanned three turns (agent.db 43c4a38b, 2026-11-25).
/// Entries decay after <see cref="Decay"/> without a call, so a later,
/// legitimate re-read of the same object is never blocked.
/// </summary>
public sealed class PaginationGuard
{
    public const int WarnThreshold = 3;
    public const int BlockThreshold = 5;

    private static readonly TimeSpan Decay = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset>? _clock;

    public PaginationGuard(Func<DateTimeOffset>? clock = null) => _clock = clock;

    public PaginationVerdict BeforeCall(string toolName, string? argumentsJson)
    {
        var key = ToolCallIdentity.Canonical(toolName, argumentsJson, ignorePageArguments: true);
        if (key is null)
            return PaginationVerdict.Proceed;

        var now = _clock?.Invoke() ?? DateTimeOffset.UtcNow;
        if (!_entries.TryGetValue(key, out var entry)
            || now - entry.LastSeenUtc > Decay)
        {
            _entries[key] = entry = new Entry();
        }

        entry.LastSeenUtc = now;
        entry.Calls++;

        if (entry.Calls > BlockThreshold)
        {
            return new PaginationVerdict(
                PaginationGuardAction.Block,
                entry.Calls,
                "This object was already read " + BlockThreshold + " times with different " +
                "limit/offset in this conversation — one more page will not add anything. " +
                "[Action: read the stored full result once (read_tool_result with the " +
                "resultHandle and a path/offset), or make ONE call with a large limit, " +
                "or move on to the next step of the task]");
        }

        if (entry.Calls > WarnThreshold)
        {
            return new PaginationVerdict(
                PaginationGuardAction.Warn,
                entry.Calls,
                "This is read #" + entry.Calls + " of the same object with only limit/offset " +
                "changed. The digest already describes the whole set; page it through " +
                "read_tool_result(resultHandle) instead of calling the tool again, or make " +
                "one call with a large limit.");
        }

        return PaginationVerdict.Proceed;
    }

    private sealed class Entry
    {
        public int Calls;

        public DateTimeOffset LastSeenUtc;
    }
}
