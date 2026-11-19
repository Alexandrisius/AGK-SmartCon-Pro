using System.Globalization;
using System.Text;

namespace SmartCon.Core.Models.Agent;

/// <summary>
/// Read-only helpers for model strings that may contain invisible characters.
/// A string that is safe for the prompt is not necessarily distinguishable for
/// the user: two sheet numbers can differ only by a zero-width or control code
/// unit. Tools expose the visual form, the exact code points and a flag so the
/// model can identify such values instead of silently deduplicating them.
/// </summary>
public static class AgentTextAudit
{
    private const int MaxCodePoints = 64;

    public static bool HasInvisibleCharacters(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        var text = value!;
        for (var i = 0; i < text.Length; i++)
        {
            if (IsInvisible(text[i]))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The visible glyphs of a model string: control/format characters are
    /// dropped and Unicode space separators are collapsed to one ASCII space.
    /// </summary>
    public static string Visual(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var text = value!;
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (IsInvisible(ch))
                continue;

            if (char.IsHighSurrogate(ch)
                && i + 1 < text.Length
                && char.IsLowSurrogate(text[i + 1]))
            {
                if (pendingSpace && builder.Length > 0)
                    builder.Append(' ');

                pendingSpace = false;
                builder.Append(ch).Append(text[i + 1]);
                i++;
                continue;
            }

            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.SpaceSeparator)
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace && builder.Length > 0)
                builder.Append(' ');

            pendingSpace = false;
            builder.Append(ch);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Exact UTF-16 code points (surrogate pairs are combined) for values that
    /// cannot be represented unambiguously as visible text.
    /// </summary>
    public static IReadOnlyList<int> CodePoints(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return Array.Empty<int>();

        var text = value!;
        var points = new List<int>(System.Math.Min(text.Length, MaxCodePoints));
        for (var i = 0; i < text.Length && points.Count < MaxCodePoints; i++)
        {
            var ch = text[i];
            if (char.IsHighSurrogate(ch)
                && i + 1 < text.Length
                && char.IsLowSurrogate(text[i + 1]))
            {
                points.Add(char.ConvertToUtf32(ch, text[i + 1]));
                i++;
                continue;
            }

            points.Add(ch);
        }

        return points;
    }

    private static bool IsInvisible(char ch) =>
        char.IsControl(ch)
        || CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.Format;
}