using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmartCon.Core.Models.Agent;

/// <summary>
/// Neutralizes control and bidirectional formatting characters in text that
/// comes from the Revit model before it reaches the LLM prompt.
/// </summary>
public static class AgentTextSanitizer
{
    private const char Replacement = '\uFFFD';

    public const int DefaultMaxStringLength = 400;

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var text = value!;
        StringBuilder? builder = null;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (IsAllowed(ch))
            {
                builder?.Append(ch);
                continue;
            }

            builder ??= new StringBuilder(text.Length).Append(text, 0, i);
            builder.Append(Replacement);
        }

        return builder?.ToString() ?? text;
    }

    /// <summary>
    /// Sanitizes every string value of a JSON payload and caps its length, so a
    /// malicious element name cannot flood the context or smuggle instructions
    /// beyond a bounded size. Falls back to plain sanitization on invalid JSON.
    /// </summary>
    public static string SanitizeJson(string? json, int maxStringLength = DefaultMaxStringLength)
    {
        if (string.IsNullOrEmpty(json))
            return string.Empty;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json!);
        }
        catch (JsonException)
        {
            return Sanitize(json);
        }

        if (node is null)
            return string.Empty;

        SanitizeNode(node, System.Math.Max(32, maxStringLength));
        return node.ToJsonString();
    }

    private static void SanitizeNode(JsonNode node, int maxStringLength)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var pair in obj.ToList())
                {
                    if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        obj[pair.Key] = JsonValue.Create(Cap(Sanitize(text), maxStringLength));
                        continue;
                    }

                    if (pair.Value is not null)
                        SanitizeNode(pair.Value, maxStringLength);
                }

                break;

            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    var item = array[index];
                    if (item is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        array[index] = JsonValue.Create(Cap(Sanitize(text), maxStringLength));
                        continue;
                    }

                    if (item is not null)
                        SanitizeNode(item, maxStringLength);
                }

                break;
        }
    }

    private static string Cap(string value, int max) =>
        value.Length <= max ? value : value.Substring(0, max) + "…";

    private static bool IsAllowed(char ch)
    {
        if (char.IsControl(ch) && ch is not '\r' and not '\n' and not '\t')
            return false;

        return ch switch
        {
            '\u200E' or '\u200F' or '\u061C' => false,
            '\u202A' or '\u202B' or '\u202C' or '\u202D' or '\u202E' => false,
            '\u2066' or '\u2067' or '\u2068' or '\u2069' => false,
            _ => true
        };
    }
}