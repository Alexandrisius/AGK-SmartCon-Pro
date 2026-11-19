using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// Stable fingerprints of the prompt parts that must not change between turns
/// (runbook H1-13). The static system prompt and the sorted tool set are hashed
/// separately so a view change or a new tool set invalidates the cache
/// deliberately, while an active-view change does not.
/// </summary>
public static class AgentPromptHash
{
    /// <summary>SHA-256 hex (lowercase) of the immutable system-prompt part.</summary>
    public static string ComputeStatic(string staticPrompt) =>
        Hash(staticPrompt ?? string.Empty);

    /// <summary>
    /// SHA-256 hex of (name + input schema) pairs sorted by name: reordering the
    /// same tools yields the same hash, any schema change yields a new one.
    /// </summary>
    public static string ComputeToolSet(IReadOnlyList<AgentToolDefinition> tools)
    {
        if (tools is null) throw new ArgumentNullException(nameof(tools));
        return Hash(CanonicalToolSet(tools));
    }

    /// <summary>SHA-256 hex of the static prompt plus the canonical tool set.</summary>
    public static string Compute(
        string staticPrompt,
        IReadOnlyList<AgentToolDefinition> tools)
    {
        if (tools is null) throw new ArgumentNullException(nameof(tools));
        return Hash((staticPrompt ?? string.Empty) + "\n--tools--\n" + CanonicalToolSet(tools));
    }

    private static string CanonicalToolSet(IReadOnlyList<AgentToolDefinition> tools)
    {
        var builder = new StringBuilder();
        foreach (var tool in tools.OrderBy(tool => tool.Name, StringComparer.Ordinal))
        {
            builder.Append(tool.Name ?? string.Empty).Append('\n');
            builder.Append(CanonicalSchema(tool.InputSchemaJson)).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Schema JSON with recursively sorted object keys: the same logical schema
    /// always hashes to the same value regardless of how the author ordered its
    /// properties (P.4). Array order, numbers and strings are preserved.
    /// </summary>
    private static string CanonicalSchema(string? schemaJson)
    {
        if (string.IsNullOrWhiteSpace(schemaJson))
            return string.Empty;

        using var document = JsonDocument.Parse(schemaJson);
        var builder = new StringBuilder(schemaJson!.Length);
        WriteCanonical(document.RootElement, builder);
        return builder.ToString();
    }

    private static void WriteCanonical(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var firstProperty = true;
                foreach (var property in element.EnumerateObject()
                             .OrderBy(candidate => candidate.Name, StringComparer.Ordinal))
                {
                    if (!firstProperty)
                        builder.Append(',');
                    firstProperty = false;
                    builder.Append(JsonSerializer.Serialize(property.Name)).Append(':');
                    WriteCanonical(property.Value, builder);
                }

                builder.Append('}');
                break;

            case JsonValueKind.Array:
                builder.Append('[');
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem)
                        builder.Append(',');
                    firstItem = false;
                    WriteCanonical(item, builder);
                }

                builder.Append(']');
                break;

            case JsonValueKind.String:
                builder.Append(JsonSerializer.Serialize(element.GetString()));
                break;

            case JsonValueKind.Null:
                builder.Append("null");
                break;

            default:
                builder.Append(element.GetRawText());
                break;
        }
    }

    private static string Hash(string value)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
        var builder = new StringBuilder(hash.Length * 2);
        foreach (var item in hash)
            builder.Append(item.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return builder.ToString();
    }
}
