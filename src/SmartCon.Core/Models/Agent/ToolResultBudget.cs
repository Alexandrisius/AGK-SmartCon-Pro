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

    private static readonly string[] TrimmableArrays =
    {
        "elements", "views", "levels", "groups", "parameters", "cards",
        "changed", "skipped", "failed", "items", "templates"
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Serialize(ToolResult result, int maxCharacters = DefaultMaxCharacters)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));

        var json = JsonSerializer.Serialize(result, SerializerOptions);
        if (json.Length <= maxCharacters)
            return json;

        return Trim(json, maxCharacters);
    }

    public static string Trim(string json, int maxCharacters = DefaultMaxCharacters)
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
            return AddEnvelope(root, maxCharacters);

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
                return AddEnvelope(root, maxCharacters);

            var keep = largest.Array.Count / 2;
            while (largest.Array.Count > keep)
                largest.Array.RemoveAt(largest.Array.Count - 1);

            data[largest.Name + "Returned"] = largest.Array.Count;
            data[largest.Name + "Total"] = largest.OriginalCount;
            data["truncated"] = true;
            data["hint"] = TruncationHint;
        }
    }

    private static string AddEnvelope(JsonObject root, int maxCharacters)
    {
        if (root["data"] is not JsonObject data)
            return root.ToJsonString(SerializerOptions);

        // No trimmable arrays left: replace the payload with a digest, keeping the
        // fields the model needs to react correctly (still valid JSON).
        var digest = new JsonObject
        {
            ["truncated"] = true,
            ["hint"] = TruncationHint
        };

        foreach (var name in new[] { "total", "returned", "offset", "nextOffset", "dryRun" })
        {
            if (data[name] is { } value)
                digest[name] = value.DeepClone();
        }

        if (data["affected"] is { } affected)
            digest["affected"] = affected.DeepClone();

        root["data"] = digest;
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
                ["hint"] = TruncationHint
            }
        };

        if (root["_repairs"] is { } repairs)
            minimal["_repairs"] = repairs.DeepClone();

        return minimal.ToJsonString(SerializerOptions);
    }
}
