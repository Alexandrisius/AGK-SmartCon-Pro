using System.Globalization;
using System.Text;

namespace SmartCon.Core.Services.FamilyManager;

/// <summary>
/// Normalizes search tokens for consistent catalog search.
/// </summary>
public static class FamilySearchNormalizer
{
    /// <summary>
    /// Normalizes a search token: lowercase, trim, remove diacritics.
    /// </summary>
    public static string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var trimmed = input.Trim().ToLowerInvariant();
        var normalized = trimmed.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);

        foreach (var c in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Splits search text into normalized tokens.
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
            return [];

        return searchText
            .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize)
            .Where(t => t.Length > 0)
            .ToArray();
    }
}
