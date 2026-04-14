using System.Text.RegularExpressions;

namespace SmartCon.Core.Math.FormulaEngine;

/// <summary>
/// Strips units of measurement from a Revit formula string before tokenization.
/// Revit API returns formulas in Internal Units (decimal feet),
/// units are decorative suffixes (mm, m, ft, in, etc.).
/// </summary>
internal static class UnitStripper
{
    // Pattern: number (integer/fractional) + optional space + unit + word boundary.
    // Order: longer suffixes first, so "mm" doesn't consume part of other suffixes.
    // Group 1 = number, group 2 = unit — keep only the number.
    private static readonly Regex UnitSuffixRegex = new(
        @"(\d+\.?\d*)\s*"
        + @"(м²|м³|мм|см|дм|mm|cm|ft|in|м)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Revit unit conversion pattern: "/ 1)" or "* 1)" — remove these
    private static readonly Regex UnitConversionRegex = new(
        @"[*/]\s*1\s*\)",
        RegexOptions.Compiled);

    internal static string Strip(string formula)
    {
        if (string.IsNullOrWhiteSpace(formula))
            return formula ?? string.Empty;

        // 1. Remove unit suffixes after numbers
        var result = UnitSuffixRegex.Replace(formula, "$1");

        // 2. Remove conversion patterns like "/ 1)", but preserve the closing parenthesis
        result = UnitConversionRegex.Replace(result, ")");

        return result;
    }
}
