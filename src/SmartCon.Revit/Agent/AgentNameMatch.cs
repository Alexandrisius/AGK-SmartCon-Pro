namespace SmartCon.Revit.Agent;

/// <summary>
/// Whitespace-insensitive name matching for family/type names (live v18: a
/// family named «RJIP Premim\u00A0WW» resolved intermittently — the model sends
/// the name with a regular space, Revit stores a non-breaking space, and
/// name-keyed lookups flip between found and not found between calls).
/// Every name comparison goes through <see cref="Normalize"/>: exotic Unicode
/// spaces (NBSP, narrow NBSP, thin/figure/punctuation space) become the plain
/// space and the ends are trimmed. The stored names are never rewritten.
/// </summary>
internal static class AgentNameMatch
{
    public static string Normalize(string? name)
    {
        if (name is null || name.Length == 0)
            return string.Empty;

        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var character in name)
        {
            builder.Append(character switch
            {
                '\u00A0' or '\u202F' or '\u2009' or '\u2007' or '\u2008' or '\u2002'
                    or '\u2003' or '\u2004' or '\u2005' or '\u2006' or '\u205F' => ' ',
                _ => character
            });
        }

        return builder.ToString().Trim();
    }

    public static bool EqualsNormalized(string? left, string? right) =>
        string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
}
