using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SmartCon.Core.Models.Agent;

/// <summary>
/// Stable fingerprint of a write action (mvp-plan §9.8 L3): canonical JSON with
/// sorted object keys and no whitespace, hashed with SHA-256. Any change in the
/// tool name, the arguments or the target document produces a different digest.
/// </summary>
public static class AgentActionDigest
{
    public static string Compute(string toolName, string argumentsJson, string documentId)
    {
        var canonical = Canonicalize(toolName, argumentsJson, documentId);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        var builder = new StringBuilder(hash.Length * 2);
        foreach (var value in hash)
            builder.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    public static string Canonicalize(string toolName, string argumentsJson, string documentId)
    {
        var normalizedArgs = CanonicalizeJson(
            string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);

        var envelope = new
        {
            tool = toolName ?? string.Empty,
            document = documentId ?? string.Empty,
            args = normalizedArgs
        };

        return CanonicalizeJson(JsonSerializer.Serialize(envelope));
    }

    /// <summary>
    /// Rewrites a JSON document with sorted object keys and no insignificant
    /// whitespace. Scalar values keep their raw representation.
    /// </summary>
    public static string CanonicalizeJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            WriteElement(writer, document.RootElement);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteElement(writer, property.Value);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteElement(writer, item);
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;

            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;

            default:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;
        }
    }
}
