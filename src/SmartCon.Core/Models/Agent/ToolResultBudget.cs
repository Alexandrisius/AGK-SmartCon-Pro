using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmartCon.Core.Models.Agent;

/// <summary>
/// Structural size control for tool results (mvp-plan §11, Phase 2 context budget).
/// When a result exceeds the character budget, arrays are shortened from the end and
/// the payload gets <c>&lt;name&gt;Returned</c>, <c>&lt;name&gt;Total</c>,
/// <c>truncated=true</c> and a <c>hint</c>. The JSON stays valid at every step —
/// the old "cut the string in half" approach produced unparseable fragments.
/// </summary>
public static class ToolResultBudget
{
    public const int DefaultMaxCharacters = 40000;

    public const string TruncationHint =
        "The result was trimmed to fit the context budget. Narrow the request " +
        "(filters, smaller limit) or continue with offset/nextOffset instead of " +
        "repeating the same call.";

    public const string BlobHint =
        "The full result is stored under resultHandle: call read_tool_result with that " +
        "handle (and offset/limit or a path) instead of re-running the same call.";

    private static readonly string[] TrimmableArrays =
    {
        "elements", "views", "levels", "groups", "parameters", "cards",
        "changed", "skipped", "failed", "items", "templates",
        "families", "types", "matchedTypes"
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Full JSON without any trimming (stored as a tool-result blob).</summary>
    public static string SerializeFull(ToolResult result)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));
        return JsonSerializer.Serialize(result, SerializerOptions);
    }

    public static string Serialize(
        ToolResult result,
        int maxCharacters = DefaultMaxCharacters,
        string? resultHandle = null)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));

        var json = SerializeFull(result);
        if (json.Length <= maxCharacters)
            return json;

        return Trim(json, maxCharacters, resultHandle);
    }

    public static string Trim(
        string json,
        int maxCharacters = DefaultMaxCharacters,
        string? resultHandle = null)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return json;
        }

        if (node is not JsonObject root)
            return json;

        var data = root["data"] as JsonObject;
        if (data is null)
            return json;

        var arrays = new List<(string Name, JsonArray Array, int OriginalCount)>();
        foreach (var name in TrimmableArrays)
        {
            if (data[name] is JsonArray array && array.Count > 0)
                arrays.Add((name, array, array.Count));
        }

        if (arrays.Count == 0)
            return AddEnvelope(root, maxCharacters, resultHandle);

        while (true)
        {
            var current = root.ToJsonString(SerializerOptions);
            if (current.Length <= maxCharacters)
                return current;

            var largest = arrays
                .Where(entry => entry.Array.Count > 0)
                .OrderByDescending(entry => entry.Array.Count)
                .FirstOrDefault();

            if (largest.Array is null)
                return AddEnvelope(root, maxCharacters, resultHandle);

            var keep = largest.Array.Count / 2;
            while (largest.Array.Count > keep)
                largest.Array.RemoveAt(largest.Array.Count - 1);

            data[largest.Name + "Returned"] = largest.Array.Count;
            data[largest.Name + "Total"] = largest.OriginalCount;
            data["truncated"] = true;
            data["hint"] = resultHandle is null ? TruncationHint : BlobHint;
            if (resultHandle is not null)
                data["resultHandle"] = resultHandle;
            AppendTruncationNote(
                root, largest.Name, largest.Array.Count, largest.OriginalCount, resultHandle);
        }
    }

    /// <summary>
    /// Keeps the top-level message truthful after trimming: a message like
    /// "Returned details for 10 element(s)" must not be sent together with a
    /// one-element payload.
    /// </summary>
    private static void AppendTruncationNote(
        JsonObject root,
        string arrayName,
        int returned,
        int total,
        string? resultHandle)
    {
        if (root["message"] is not JsonValue messageValue
            || !messageValue.TryGetValue<string>(out var message)
            || message!.IndexOf("[truncated:", StringComparison.Ordinal) >= 0)
        {
            return;
        }

        root["message"] = message + " [truncated: " + returned + " of " + total + " " +
                          arrayName + " returned" +
                          (resultHandle is null
                              ? "; narrow the request"
                              : "; full result under resultHandle for read_tool_result") + "]";
    }

    private static string AddEnvelope(
        JsonObject root,
        int maxCharacters,
        string? resultHandle = null)
    {
        if (root["data"] is not JsonObject data)
            return root.ToJsonString(SerializerOptions);

        // No trimmable arrays left: replace the payload with a digest, keeping the
        // fields the model needs to react correctly (still valid JSON).
        var digest = new JsonObject
        {
            ["truncated"] = true,
            ["hint"] = resultHandle is null ? TruncationHint : BlobHint
        };

        foreach (var name in new[] { "total", "returned", "offset", "nextOffset", "dryRun" })
        {
            if (data[name] is { } value)
                digest[name] = value.DeepClone();
        }

        if (data["affected"] is { } affected)
            digest["affected"] = affected.DeepClone();

        if (resultHandle is not null)
            digest["resultHandle"] = resultHandle;

        root["data"] = digest;
        if (root["message"] is JsonValue messageValue
            && messageValue.TryGetValue<string>(out var message)
            && message!.IndexOf("[truncated:", StringComparison.Ordinal) < 0)
        {
            root["message"] = message + " [truncated: payload replaced by a digest" +
                              (resultHandle is null
                                  ? "; narrow the request"
                                  : "; full result under resultHandle") + "]";
        }

        var json = root.ToJsonString(SerializerOptions);
        if (json.Length <= maxCharacters)
            return json;

        // Extremely long message/error text: keep only the essentials.
        var minimal = new JsonObject
        {
            ["success"] = root["success"]?.DeepClone() ?? false,
            ["errorCode"] = root["errorCode"]?.DeepClone(),
            ["message"] = root["message"]?.DeepClone(),
            ["affectedCount"] = root["affectedCount"]?.DeepClone() ?? 0,
            ["data"] = new JsonObject
            {
                ["truncated"] = true,
                ["hint"] = resultHandle is null ? TruncationHint : BlobHint
            }
        };

        if (resultHandle is not null)
            ((JsonObject)minimal["data"]!)["resultHandle"] = resultHandle;

        if (root["_repairs"] is { } repairs)
            minimal["_repairs"] = repairs.DeepClone();

        return minimal.ToJsonString(SerializerOptions);
    }
}
