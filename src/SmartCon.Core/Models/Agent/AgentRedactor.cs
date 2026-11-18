using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace SmartCon.Core.Models.Agent;

/// <summary>
/// Removes credential-looking fragments before tool arguments/results are
/// persisted in agent.db or written to logs (C5: redaction). It does not touch
/// what the model sees for model data; it only protects storage.
/// </summary>
public static class AgentRedactor
{
    public const string Replacement = "[redacted]";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private static readonly Regex[] Patterns =
    {
        new(@"sk-[A-Za-z0-9_\-]{8,}", RegexOptions.Compiled),
        new(@"sk-or-v1-[A-Za-z0-9_\-]{8,}", RegexOptions.Compiled),
        new(@"Bearer\s+[A-Za-z0-9_\-\.]{8,}", RegexOptions.Compiled),
        new(
            @"(?i)(api[_-]?key|apikey|authorization|token|secret|password)\s*[""']?\s*[=:]\s*[""']?[A-Za-z0-9_\-\.]{8,}",
            RegexOptions.Compiled)
    };

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var result = value!;
        if (TryRedactJson(result, out var json))
            return json;

        foreach (var pattern in Patterns)
            result = pattern.Replace(result, Replacement);

        return result;
    }

    public static bool ContainsCredential(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        foreach (var pattern in Patterns)
        {
            if (pattern.IsMatch(value!))
                return true;
        }

        return TryFindSecretKey(value!);
    }

    /// <summary>
    /// JSON-aware pass: secret-named keys are replaced by a string marker (for
    /// any value type) and credential-shaped string values are masked in place.
    /// The payload stays valid JSON — regexes are never applied to the encoded
    /// document, only to individual string values.
    /// </summary>
    private static bool TryRedactJson(string value, out string result)
    {
        result = value;
        var trimmed = value.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
            return false;

        try
        {
            var node = JsonNode.Parse(value);
            if (node is null)
                return false;

            RedactNode(node);
            result = node.ToJsonString(SerializerOptions);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void RedactNode(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj.ToList())
                {
                    if (IsSecretKey(property.Key))
                    {
                        obj[property.Key] = Replacement;
                        continue;
                    }

                    if (property.Value is JsonValue value
                        && value.TryGetValue<string>(out var text))
                    {
                        var redacted = RedactText(text!);
                        if (!string.Equals(redacted, text, StringComparison.Ordinal))
                            obj[property.Key] = redacted;
                        continue;
                    }

                    if (property.Value is not null)
                        RedactNode(property.Value);
                }

                break;

            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    var item = array[index];
                    if (item is JsonValue jsonValue
                        && jsonValue.TryGetValue<string>(out var text))
                    {
                        var redacted = RedactText(text!);
                        if (!string.Equals(redacted, text, StringComparison.Ordinal))
                            array[index] = redacted;
                        continue;
                    }

                    if (item is not null)
                        RedactNode(item);
                }

                break;
        }
    }

    private static string RedactText(string text)
    {
        var result = text;
        foreach (var pattern in Patterns)
            result = pattern.Replace(result, Replacement);
        return result;
    }

    private static bool TryFindSecretKey(string value)
    {
        var trimmed = value.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
            return false;

        try
        {
            var node = JsonNode.Parse(value);
            return node is not null && HasSecretKey(node);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasSecretKey(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj)
                {
                    if (IsSecretKey(property.Key)
                        && property.Value is JsonValue value
                        && value.TryGetValue<string>(out var secret)
                        && !string.IsNullOrEmpty(secret))
                    {
                        return true;
                    }

                    if (property.Value is not null && HasSecretKey(property.Value))
                        return true;
                }

                return false;

            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is not null && HasSecretKey(item))
                        return true;
                }

                return false;

            default:
                return false;
        }
    }

    private static bool IsSecretKey(string key)
    {
        var normalized = key.Replace("-", string.Empty).Replace("_", string.Empty)
            .ToLowerInvariant();
        if (normalized is "apikey" or "authorization" or "token" or "secret" or "password"
            or "secretkey" or "accesstoken" or "refreshtoken" or "bearertoken")
        {
            return true;
        }

        return normalized.EndsWith("apikey", StringComparison.Ordinal)
               || normalized.EndsWith("secretkey", StringComparison.Ordinal)
               || normalized.EndsWith("password", StringComparison.Ordinal)
               || normalized.EndsWith("secret", StringComparison.Ordinal)
               || normalized.EndsWith("token", StringComparison.Ordinal);
    }
}
