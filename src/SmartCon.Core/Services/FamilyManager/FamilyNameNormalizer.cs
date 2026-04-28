using System.Globalization;
using System.Text;

namespace SmartCon.Core.Services.FamilyManager;

/// <summary>
/// Normalizes family names for search indexing.
/// Removes diacritics, converts to lowercase.
/// </summary>
public static class FamilyNameNormalizer
{
    /// <summary>
    /// Normalizes a family name for search indexing.
    /// </summary>
    public static string Normalize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var lower = name.Trim().ToLowerInvariant();
        var normalized = lower.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);

        foreach (var c in normalized)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
