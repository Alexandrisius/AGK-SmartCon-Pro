using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        if (string.IsNullOrWhiteSpace(path))
        {
            content = blob.ResultJson;
        }
        else
        {
            var resolved = ResolvePath(blob.ResultJson, path!, out var pathError);
            if (resolved is null)
                return ToolResult.Fail("invalid_arguments", pathError!);

            content = resolved;
        }

        var offset = Math.Max(0, GetInt(args, "offset", 0));
        var limit = Math.Max(1, Math.Min(GetInt(args, "limit", DefaultLimit), MaxLimit));
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
            offset,
            limit,
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
                "re-running the original tool.",
            Risk = AgentToolRisk.Read,
            SupportsDryRun = false,
            Category = "Meta",
            TimeoutMs = 15000,
            InputSchemaJson = @"{""type"":""object"",""properties"":{" +
                @"""handle"":{""type"":""string"",""minLength"":1}," +
                @"""offset"":{""type"":""integer"",""minimum"":0,""default"":0}," +
                @"""limit"":{""type"":""integer"",""minimum"":1,""maximum"":20000,""default"":8000}," +
                @"""path"":{""type"":""string"",""description"":""Dot/slash path inside the JSON, e.g. data.views or data/views""}}," +
                @"""required"":[""handle""],""additionalProperties"":false}"
        };

    private static string? ResolvePath(string json, string path, out string? error)
    {
        error = null;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            error = "The stored result is not valid JSON: " + ex.Message;
            return null;
        }

        foreach (var segment in path.Split(new[] { '.', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (node is JsonObject obj)
            {
                if (!obj.TryGetPropertyValue(segment, out node))
                {
                    error = "Path segment '" + segment + "' was not found.";
                    return null;
                }
            }
            else if (node is JsonArray array)
            {
                if (!int.TryParse(segment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                    || index < 0 || index >= array.Count)
                {
                    error = "Path segment '" + segment + "' is not a valid array index.";
                    return null;
                }

                node = array[index];
            }
            else
            {
                error = "Path segment '" + segment + "' cannot be resolved: the value is a scalar.";
                return null;
            }
        }

        return node is null
            ? "null"
            : node is JsonValue value && value.TryGetValue<string>(out var text)
                ? text
                : node.ToJsonString();
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
