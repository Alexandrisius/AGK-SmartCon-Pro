namespace SmartCon.Core.Services.Implementation;

/// <summary>
/// Corrects known Autodesk Russian-localization quirks in unit symbols
/// produced by Revit (<c>AsValueString</c> / <c>UnitFormatUtils.Format</c>).
/// Revit's RU symbol table renders the pressure unit bar as "бары", but
/// Russian metrological usage (ГОСТ 8.417) treats "бар" as indeclinable:
/// the correct display is "16 бар". Rules are trailing-token replacements,
/// applied ordinally; unknown strings pass through unchanged.
/// See #151 for root cause and workaround rationale.
/// </summary>
public static class UnitSymbolFixup
{
    private static readonly (string Wrong, string Correct)[] TrailingRules =
    [
        (" бары", " бар"),
    ];

    public static string? Correct(string? formatted)
    {
        if (string.IsNullOrEmpty(formatted))
            return formatted;

        foreach (var (wrong, correct) in TrailingRules)
        {
            if (formatted!.EndsWith(wrong, StringComparison.Ordinal))
            {
#if NET8_0_OR_GREATER
                return string.Concat(formatted.AsSpan(0, formatted.Length - wrong.Length), correct.AsSpan());
#else
                return formatted.Substring(0, formatted.Length - wrong.Length) + correct;
#endif
            }
        }

        return formatted;
    }
}
