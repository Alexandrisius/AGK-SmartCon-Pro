using System.Globalization;

namespace SmartCon.Core.Services.Implementation;

/// <summary>
/// Parses the numeric part of a Revit display string ("300 мм", "16 бар",
/// "1 200 мм", "1,200 mm", "0.164042") into a double. Used by the
/// import-validation mappers so numeric rules authored in the parameter's
/// display units (what the user sees in family properties) compare against
/// display-unit values instead of Revit internal feet.
/// <para>
/// Thousand separators are normalized BEFORE parsing: spaces/NBSP/NNBSP
/// are stripped ("1 200" → 1200), and comma/dot grouping is resolved by
/// a heuristic (mixed separators → the LAST one is decimal; single comma
/// with a 3-digit tail and ≥2 leading digits counts as thousands — en-US
/// "1,200" → 1200; RU "16,5" → 16.5).
/// </para>
/// <para>
/// Returns <c>null</c> when the string is not a plain decimal number:
/// imperial formats ("1'-6\"", "1/2\"") are rejected so the caller falls
/// back to the raw internal-unit value instead of comparing a wrong
/// number.
/// </para>
/// </summary>
public static class DisplayValueParser
{
    private const char Nbsp = ' ';
    private const char Nnbsp = ' ';

    public static double? TryParseNumber(string? displayText)
    {
        if (string.IsNullOrWhiteSpace(displayText))
        {
            return null;
        }

        // Take the leading run up to the first letter (the unit symbol) —
        // digits, separators, sign, and spaces (possible thousands groups).
        var span = displayText.AsSpan().TrimStart();
        var end = 0;
        var seenDigit = false;
        while (end < span.Length)
        {
            var c = span[end];
            if (char.IsDigit(c))
            {
                seenDigit = true;
                end++;
                continue;
            }

            if (c == '.' || c == ',' || c == ' ' || c == Nbsp || c == Nnbsp || c == '-' || c == '+')
            {
                end++;
                continue;
            }

            break;
        }

        if (!seenDigit)
        {
            return null;
        }

        // Imperial format ("1'-6\"", "1/2\""): the decimal prefix is only
        // part of the value — returning it would be wrong. The stopper
        // character right after the numeric run decides.
        if (end < span.Length && (span[end] == '\'' || span[end] == '"' || span[end] == '/'))
        {
            return null;
        }

        var raw = span[..end].Trim().ToString();
        if (raw.Length == 0)
        {
            return null;
        }

        if (raw.Contains('\'') || raw.Contains('"') || raw.Contains('/'))
        {
            return null;
        }

        // The unit symbol after the number disambiguates a single comma
        // with a 3-digit tail ("1,200 mm" en-thousands vs "16,500 бар"
        // ru-decimal): Cyrillic unit → RU decimal comma.
        var cyrillicUnit = end < span.Length && span[end] >= 'Ѐ';

        // Strip spaces (incl. NBSP/NNBSP) — thousands groups.
        var compact = raw
            .Replace(" ", string.Empty)
            .Replace(Nbsp.ToString(), string.Empty)
            .Replace(Nnbsp.ToString(), string.Empty);

        var normalized = NormalizeSeparators(compact, cyrillicUnit);
        if (normalized is null)
        {
            return null;
        }

        if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return null;
    }

    /// <summary>
    /// Resolves comma/dot grouping to an invariant decimal string:
    /// both present → the LAST one is decimal, the other is stripped;
    /// single separator kind → decimal when it clearly is one (comma with
    /// a non-3-digit tail, or any single dot usage), otherwise a
    /// thousands separator (stripped).
    /// </summary>
    private static string? NormalizeSeparators(string text, bool cyrillicUnit)
    {
        var lastDot = text.LastIndexOf('.');
        var lastComma = text.LastIndexOf(',');

        if (lastDot >= 0 && lastComma >= 0)
        {
            // Mixed: the rightmost separator is the decimal mark.
            if (lastDot > lastComma)
            {
                return text.Replace(",", string.Empty);
            }

            return text.Replace(".", string.Empty).Replace(',', '.');
        }

        if (lastComma >= 0)
        {
            var commaCount = text.Count(c => c == ',');
            if (commaCount > 1)
            {
                // "1,234,567" — grouping.
                return text.Replace(",", string.Empty);
            }

            var tail = text[(lastComma + 1)..];
            if (tail.Length == 3 && tail.All(char.IsDigit) && !cyrillicUnit)
            {
                // "1,200 mm" — en-US thousands (Cyrillic unit handled
                // above: "16,500 бар" stays a ru-decimal 16.5).
                return text.Replace(",", string.Empty);
            }

            // RU-style decimal comma.
            return text.Replace(',', '.');
        }

        if (lastDot >= 0)
        {
            var dotCount = text.Count(c => c == '.');
            if (dotCount > 1)
            {
                // "1.234.567" — de thousands grouping.
                return text.Replace(".", string.Empty);
            }

            // Plain invariant decimal point.
            return text;
        }

        return text;
    }
}
