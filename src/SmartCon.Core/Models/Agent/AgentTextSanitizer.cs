using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmartCon.Core.Models.Agent;

/// <summary>
/// Neutralizes control and bidirectional formatting characters in text that
/// comes from the Revit model before it reaches the LLM prompt. Unsafe code
/// units are escaped visibly ("\u000B") instead of being replaced by U+FFFD:
/// two different model strings must never collapse into the same prompt text.
/// </summary>
public static class AgentTextSanitizer
{
    public const int DefaultMaxStringLength = 400;

    /// <summary>
    /// JSON properties whose string values are harness-paged text: pack skill
    /// manuals, <c>read_tool_result</c> pages and the tool's own message. They
    /// are sanitized but never capped — capping a manual at 400 characters made
    /// the model read only its first paragraph, capping a page broke paging
    /// (#400), and capping a message cut off its reason and the next step.
    /// </summary>
    private static readonly HashSet<string> TrustedStringKeys =
        new(StringComparer.Ordinal) { "markdown", "content", "message" };

    /// <summary>
    /// The sanitized JSON is the text the model reads. The default encoder
    /// writes every non-ASCII character as <c>\uXXXX</c>: a Cyrillic name cost
    /// six characters per letter, models misread the escapes (T as C), and the
    /// inflated length pushed whole results into digests.
    /// </summary>
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var text = value!;
        StringBuilder? builder = null;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    builder?.Append(ch).Append(text[i + 1]);
                    i++;
                    continue;
                }

                builder ??= new StringBuilder(text.Length + 8).Append(text, 0, i);
                AppendEscape(builder, ch);
                continue;
            }

            if (char.IsLowSurrogate(ch) || !IsAllowed(ch))
            {
                builder ??= new StringBuilder(text.Length + 8).Append(text, 0, i);
                AppendEscape(builder, ch);
                continue;
            }

            builder?.Append(ch);
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

        try
        {
            var node = JsonNode.Parse(json!);
            if (node is null)
                return string.Empty;

            SanitizeNode(node, System.Math.Max(32, maxStringLength));
            return node.ToJsonString(WireOptions);
        }
        catch (JsonException)
        {
            return Sanitize(json);
        }
        catch (InvalidOperationException)
        {
            // A lone surrogate escape parsed as JSON but cannot be read back as
            // a .NET string; keep the payload instead of failing the tool chain.
            return Sanitize(json);
        }
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
                        // Harness-authored manuals (skills[].markdown) are sanitized but
                        // not capped: 400 characters would cut the manual to its first
                        // paragraph (#400). Everything else keeps the injection cap.
                        var sanitized = Sanitize(text);
                        obj[pair.Key] = JsonValue.Create(
                            TrustedStringKeys.Contains(pair.Key)
                                ? sanitized
                                : Cap(sanitized, maxStringLength));
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

    private static void AppendEscape(StringBuilder builder, char ch)
    {
        builder.Append("\\u");
        builder.Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
    }

    private static string Cap(string value, int max)
    {
        if (value.Length <= max)
            return value;

        var cut = max;
        if (cut > 0
            && cut < value.Length
            && char.IsHighSurrogate(value[cut - 1])
            && char.IsLowSurrogate(value[cut]))
        {
            cut--;
        }

        return cut <= 0
            ? "…"
            : value.Substring(0, cut) + "…";
    }

    private static bool IsAllowed(char ch)
    {
        if (char.IsControl(ch) && ch is not '\r' and not '\n' and not '\t')
            return false;

        // Every Unicode format character is invisible by definition: zero-width
        // spaces/joiners, bidi controls, BOM. Escaping all of them keeps distinct
        // model strings distinct in the prompt.
        return CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.Format;
    }
}