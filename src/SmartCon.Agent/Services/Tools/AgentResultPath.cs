using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmartCon.Agent.Services.Tools;

/// <summary>
/// Resolves a dot/slash path inside the JSON of a stored tool result. The
/// reader (<c>read_tool_result</c>) and the argument reference of the
/// orchestrator address a stored result the same way.
/// </summary>
internal static class AgentResultPath
{
    /// <summary>
    /// The node at <paramref name="path"/>, or null with <paramref name="error"/>
    /// set when the result is not JSON or the path does not exist.
    /// </summary>
    internal static JsonNode? Resolve(string json, string path, out string? error)
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
                    || index < 0
                    || index >= array.Count)
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

        return node;
    }

    /// <summary>The resolved value as text: a JSON string as itself, anything else as JSON.</summary>
    internal static string ToText(JsonNode? node) =>
        node is null
            ? "null"
            : node is JsonValue value && value.TryGetValue<string>(out var text)
                // net48 ref assemblies lack [NotNullWhen] on TryGetValue.
                ? text!
                : node.ToJsonString();
}
