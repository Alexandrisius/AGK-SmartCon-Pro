using System.Text;

namespace SmartCon.Core.Models.Agent;

/// <summary>
/// Neutralizes control and bidirectional formatting characters in text that
/// comes from the Revit model before it reaches the LLM prompt.
/// </summary>
public static class AgentTextSanitizer
{
    private const char Replacement = '\uFFFD';

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