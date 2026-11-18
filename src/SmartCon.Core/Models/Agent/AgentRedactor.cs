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

    private static readonly Regex[] Patterns =
    {
        new(@"sk-[A-Za-z0-9_\-]{8,}", RegexOptions.Compiled),
        new(@"sk-or-v1-[A-Za-z0-9_\-]{8,}", RegexOptions.Compiled),
        new(@"Bearer\s+[A-Za-z0-9_\-\.]{8,}", RegexOptions.Compiled),
        new(@"(?i)(api[_-]?key|apikey|authorization|token)\s*[=:]\s*[""']?[A-Za-z0-9_\-\.]{8,}",
            RegexOptions.Compiled)
    };

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var result = value!;
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

        return false;
    }
}
