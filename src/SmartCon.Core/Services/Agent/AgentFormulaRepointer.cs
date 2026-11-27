using System.Text;
using System.Text.RegularExpressions;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// Rewrites parameter references inside a Revit family formula from the
/// source names of a mapping list to their target names (live v18: a
/// shared-parameter migration needs the customer's parameters driven by the
/// SAME formulas as the originals — plain mode=formula copies the formula
/// verbatim and keeps the duplicates chained to the parameters that a later
/// stage removes).
/// <para>
/// Mechanics that make the rewrite safe: quoted literals ("шт.", size_lookup
/// column names) are never touched; replacements are applied longest-first so
/// «Наименование краткое» is not eaten by «Наименование»; identifier
/// boundaries are respected («X2» is not hit by a «X» rule); matching is
/// case-insensitive the same way Revit resolves parameter names.
/// </para>
/// </summary>
public static class AgentFormulaRepointer
{
    /// <summary>
    /// Returns the formula with every source name replaced by its target name.
    /// <paramref name="replacements"/> is a list of (source, target) pairs;
    /// longer source names win when one name is a prefix of another. The
    /// <paramref name="replaced"/> output lists the pairs that actually matched
    /// (for honest preview warnings), in application order. Parameter names
    /// with spaces are fine — the boundary guards anchor whole identifiers.
    /// </summary>
    public static string Repoint(
        string formula,
        IReadOnlyList<KeyValuePair<string, string>> replacements,
        out IReadOnlyList<string> replaced)
    {
        replaced = Array.Empty<string>();
        if (string.IsNullOrEmpty(formula) || replacements.Count == 0)
        {
            return formula;
        }

        var ordered = replacements
            .OrderByDescending(pair => pair.Key.Length)
            .Where(pair => pair.Key.Length > 0 && !pair.Key.Contains('"'))
            .ToList();

        var segments = SplitByQuotes(formula);
        var applied = new List<string>();
        var builder = new StringBuilder(formula.Length);
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            if (index % 2 == 1)
            {
                // A quoted literal: text values and size_lookup column names
                // are data, not parameter references.
                builder.Append('"').Append(segment).Append('"');
                continue;
            }

            foreach (var pair in ordered)
            {
                var pattern = @"(?<![\p{L}\p{N}_])" + Regex.Escape(pair.Key) + @"(?![\p{L}\p{N}_])";
                if (Regex.IsMatch(segment, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    segment = Regex.Replace(
                        segment, pattern, pair.Value,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (!applied.Contains(pair.Key, StringComparer.Ordinal))
                    {
                        applied.Add(pair.Key);
                    }
                }
            }

            builder.Append(segment);
        }

        replaced = applied;
        return builder.ToString();
    }

    /// <summary>
    /// The ×1/÷1 unit trick (Autodesk: "the easiest way to neutralize a unit
    /// is by dividing by 1"; explicit constants are best practice because a
    /// bare 1 makes Revit guess the units). Stripping: a dimensioned formula
    /// wrapped as "(F) / 1 mm" becomes a bare number (of millimetres). Adding:
    /// a bare number wrapped as "(F) * 1 mm" becomes a length.
    /// </summary>
    public static string WrapWithUnit(string formula, string unit, bool addUnits) =>
        addUnits
            ? "(" + formula + ") * 1 " + unit
            : "(" + formula + ") / 1 " + unit;

    /// <summary>A unit constant a formula can carry: mm, m2, kg, MPa, °C…</summary>
    public static bool IsValidUnitToken(string unit) =>
        !string.IsNullOrWhiteSpace(unit)
        && unit.Length <= 8
        && Regex.IsMatch(unit, @"^[\p{L}\p{N}°]+$", RegexOptions.CultureInvariant);

    /// <summary>Splits the formula into out-of-quotes (even index) and quoted
    /// literal (odd index) segments.</summary>
    private static List<string> SplitByQuotes(string formula)
    {
        var segments = new List<string>();
        var start = 0;
        for (var index = 0; index < formula.Length; index++)
        {
            if (formula[index] != '"')
            {
                continue;
            }

            segments.Add(formula.Substring(start, index - start));
            start = index + 1;
        }

        segments.Add(formula.Substring(start));
        return segments;
    }
}
