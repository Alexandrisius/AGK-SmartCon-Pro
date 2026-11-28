using System.Text.Json;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Agent.Services.Tools;

/// <summary>
/// Reads a stored full tool result by handle (runbook H1-14). The model gets a
/// trimmed preview plus <c>resultHandle</c> in the tool envelope and fetches the
/// rest in pages instead of re-running the tool.
/// </summary>
public sealed class ReadToolResultTool : IAgentTool
{
    public const string ToolName = "read_tool_result";

    private const int DefaultLimit = 8000;
    private const int MaxLimit = 20000;

    private readonly IAgentToolResultBlobStore _store;

    public ReadToolResultTool(IAgentToolResultBlobStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        Definition = BuildDefinition();
    }

    public AgentToolDefinition Definition { get; }

    public async Task<ToolResult> ExecuteAsync(
        AgentToolContext context,
        JsonElement args,
        CancellationToken ct)
    {
        var handle = GetString(args, "handle");
        if (string.IsNullOrWhiteSpace(handle))
            return ToolResult.Fail("invalid_arguments", "handle is required.");

        if (context.ConversationId == Guid.Empty)
            return ToolResult.Fail(
                "tool_failed",
                "No active conversation: stored tool results are scoped to a chat.");

        var blob = await _store
            .GetAsync(context.ConversationId, handle!, ct)
            .ConfigureAwait(false);
        if (blob is null)
        {
            return ToolResult.Fail(
                "result_not_found",
                "No stored result for handle '" + handle + "'. It may have expired, belong " +
                "to another chat, or the original result was never trimmed.");
        }

        string content;
        var path = GetString(args, "path");
        var project = GetStringArray(args, "project");
        var projected = project.Count > 0;
        if (projected)
        {
            var selection = AgentResultPath.Project(
                blob.ResultJson, path, project, out var projectError);
            if (selection is null)
                return ToolResult.Fail("invalid_arguments", PathError(projectError, blob.ResultJson));

            content = AgentResultPath.ToText(selection);
        }
        else if (string.IsNullOrWhiteSpace(path))
        {
            content = blob.ResultJson;
        }
        else
        {
            var resolved = ResolvePath(blob.ResultJson, path!, out var pathError);
            if (resolved is null)
                return ToolResult.Fail("invalid_arguments", PathError(pathError, blob.ResultJson));

            content = resolved;
        }

        var offset = Math.Max(0, GetInt(args, "offset", 0));
        // The schema no longer rejects a large limit: it is cut to the maximum
        // here and the answer says so, instead of costing the model a round.
        var requestedLimit = GetInt(args, "limit", DefaultLimit);
        var limit = Math.Max(1, Math.Min(requestedLimit, MaxLimit));
        var limitCappedTo = requestedLimit > MaxLimit ? MaxLimit : (int?)null;
        if (offset >= content.Length)
        {
            return ToolResult.Fail(
                "offset_out_of_range",
                "offset " + offset + " is beyond the stored result length " + content.Length + ".");
        }

        var length = Math.Min(limit, content.Length - offset);
        var nextOffset = offset + length < content.Length ? offset + length : (int?)null;
        var payload = new
        {
            handle,
            path,
            projected,
            project,
            offset,
            limit,
            limitCappedTo,
            total = content.Length,
            returned = length,
            nextOffset,
            truncated = nextOffset is not null,
            content = content.Substring(offset, length),
            hint = nextOffset is null
                ? "End of the stored result."
                : "Call read_tool_result with offset=" + nextOffset + " to continue."
        };

        SmartConLogger.Debug(
            "read_tool_result: handle=" + handle + ", path=" + (path ?? "<root>") +
            ", offset=" + offset + ", returned=" + length + "/" + content.Length);

        return ToolResult.Ok(
            JsonSerializer.SerializeToElement(payload),
            "Returned " + length + " of " + content.Length + " characters.");
    }

    private static AgentToolDefinition BuildDefinition() =>
        new()
        {
            Name = ToolName,
            Description =
                "Reads the full stored result of an earlier tool call by resultHandle. Tool " +
                "results that exceed the context budget are trimmed and carry resultHandle; use " +
                "this tool with offset/limit (and an optional path inside the JSON) instead of " +
                "re-running the original tool. offset/limit are measured in characters " +
                "(default 8000 ≈ 2–3k tokens), not tokens; limit is at most 20000 - larger " +
                "values are cut to 20000. Path segments accept both data.views.1 and data.views[1]; " +
                "a negative index counts from the end. project:[...] returns ONLY the named paths " +
                "and maps a property over arrays (parameters.name → array of names), so a large " +
                "result can be aggregated without paging it into the context: " +
                "project:[\"signatureDigest\",\"parameters.name\",\"count\"]. To PASS a part of a " +
                "stored result to another tool, do not read it: put the reference object " +
                "{\"$result\":\"<handle>\",\"path\":\"<path>\"} in place of that argument.",
            Risk = AgentToolRisk.Read,
            SupportsDryRun = false,
            Category = "Meta",
            TimeoutMs = 15000,
            InputSchemaJson = @"{""type"":""object"",""properties"":{" +
                @"""handle"":{""type"":""string"",""minLength"":1}," +
                @"""offset"":{""type"":""integer"",""minimum"":0,""default"":0}," +
                @"""limit"":{""type"":""integer"",""minimum"":1,""default"":8000,""description"":""At most 20000; larger values are cut to 20000 (see limitCappedTo in the result)""}," +
                @"""path"":{""type"":""string"",""description"":""Dot/slash path inside the JSON, e.g. data.views or data/views; arrays accept [n], e.g. data.groups[0]""}," +
                @"""project"":{""type"":""array"",""items"":{""type"":""string""},""maxItems"":50,""description"":""Return only these paths (relative to path). A property over an array maps over its elements: parameters.name -> array of names""}}," +
                @"""required"":[""handle""],""additionalProperties"":false}"
        };

    private static string? ResolvePath(string json, string path, out string? error)
    {
        var node = AgentResultPath.Resolve(json, path, out error);
        return error is null ? AgentResultPath.ToText(node) : null;
    }

    /// <summary>The not-found error carries the keys the caller CAN address:
    /// the stored results are envelope-wrapped, so a path without the envelope
    /// root ("families.name" instead of "data.families") used to read as a bare
    /// "not found" with no hint where the data actually lives.</summary>
    private static string PathError(string? error, string resultJson)
    {
        var hint = AgentResultPath.RootsHint(resultJson);
        return (error ?? "The path was not found.")
               + (hint.Length > 0 ? " " + hint : string.Empty)
               + " [Action: prefix the path with the envelope root (e.g. path:\"data.families\") " +
                 "and project relative to that root]";
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

    private static IReadOnlyList<string> GetStringArray(JsonElement args, string property)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    items.Add(text!);
            }
        }

        return items;
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
