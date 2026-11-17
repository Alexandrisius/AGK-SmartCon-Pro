using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SmartCon.Agent.Services;

/// <summary>
/// Serialization options for tool results. Relaxed escaping keeps Cyrillic and
/// other non-ASCII model data human-readable (both in the LLM payload and in the
/// tool-details panel) instead of \uXXXX escapes.
/// </summary>
internal static class AgentToolResultFormatter
{
    private static readonly Regex UnicodeEscape = new(
        @"(?<!\\)\\u([0-9a-fA-F]{4})",
        RegexOptions.Compiled);

    internal static readonly JsonSerializerOptions CompactOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal static string Pretty(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return string.Empty;

        try
        {
            using var document = JsonDocument.Parse(json!);
            var root = document.RootElement;

            // Old (pre-relaxed-encoder) truncation envelopes kept the raw JSON
            // in a "preview" string with \uXXXX escapes. Show the actual preview
            // document instead of a giant escaped string.
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("truncated", out var truncated)
                && truncated.ValueKind == JsonValueKind.True
                && root.TryGetProperty("preview", out var preview)
                && preview.ValueKind == JsonValueKind.String
                && preview.GetString() is { Length: > 0 } previewJson)
            {
                try
                {
                    using var previewDocument = JsonDocument.Parse(previewJson);
                    return Unescape(JsonSerializer.Serialize(previewDocument.RootElement, PrettyOptions));
                }
                catch (JsonException)
                {
                    // The stored preview is a cut-off JSON fragment. Show its
                    // decoded text without the envelope/escape noise.
                    return Unescape(previewJson);
                }
            }

            return Unescape(JsonSerializer.Serialize(root, PrettyOptions));
        }
        catch (JsonException)
        {
            return Unescape(json!);
        }
    }

    private static string Unescape(string text) =>
        UnicodeEscape.Replace(
            text,
            match => ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString());
}