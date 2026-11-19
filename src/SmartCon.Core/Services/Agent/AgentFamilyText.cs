using System.Text.RegularExpressions;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// Pure text helpers for the family editing pack: lookup-table extraction from
/// formulas, user-query normalization for live search and file-name sanitizing.
/// No Revit API — unit-testable in SmartCon.Tests.
/// </summary>
public static class AgentFamilyText
{
    private static readonly Regex LookupTableRegex = new(
        "size_lookup\\s*\\(\\s*\"(?<table>[^\"]+)\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Extracts the distinct size_lookup table names referenced by a formula.</summary>
    public static IReadOnlyList<string> ExtractLookupTables(string? formula)
    {
        if (string.IsNullOrWhiteSpace(formula))
            return Array.Empty<string>();

        var tables = new List<string>();
        foreach (Match match in LookupTableRegex.Matches(formula!))
        {
            var table = match.Groups["table"].Value;
            if (!string.IsNullOrWhiteSpace(table) && !tables.Contains(table))
                tables.Add(table);
        }

        return tables;
    }

    /// <summary>Lowercase, trims and normalizes ё→е for parameter-name matching.</summary>
    public static string NormalizeForMatch(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value!
            .Trim()
            .ToLowerInvariant()
            .Replace('ё', 'е');
    }

    public static bool ContainsNormalized(string? haystack, string needle)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle))
            return false;

        return NormalizeForMatch(haystack)
            .IndexOf(NormalizeForMatch(needle), StringComparison.Ordinal) >= 0;
    }

    /// <summary>Replaces invalid file-name characters so a family name can be a backup file.</summary>
    public static string SanitizeFileName(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length);
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        foreach (var ch in name)
        {
            var keep = true;
            for (var i = 0; i < invalid.Length; i++)
            {
                if (invalid[i] == ch)
                {
                    keep = false;
                    break;
                }
            }

            builder.Append(keep ? ch : '_');
        }

        var result = builder.ToString().Trim();
        return result.Length == 0 ? "family" : result;
    }
}