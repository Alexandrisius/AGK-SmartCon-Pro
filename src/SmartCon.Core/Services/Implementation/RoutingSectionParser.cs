using System.Globalization;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Implementation;

/// <summary>
/// File-free routing backfill (ADR-072, Phase 2b): parses the ROUTING and
/// FAMKEY canonical sections stored in <c>catalog_versions.section_strings</c>
/// (V33) back into routing snapshots — the primary backfill source for
/// pre-V34 catalog versions (opening the staged mini is only the fallback
/// for versions without section strings). Handles both manager int group
/// tokens (pre-FHV19 and FHV19) and FHV19 <c>"Param:&lt;BIP&gt;"</c> keys,
/// and reverses the canonical escaping (<c>%7C</c>→<c>|</c>,
/// <c>%25</c>→<c>%</c>).
/// </summary>
public static class RoutingSectionParser
{
    private const string RoutingSectionPrefix = "ROUTING|";
    private const string FamKeySectionPrefix = "FAMKEY|";
    // Marker collision (accepted, same class as INVALID/READERROR in VALUES,
    // ADR-056): a segment/fitting literally named "NOPART" parses back as a
    // no-part rule. vanishingly rare; documented, not handled.
    private const string NoPartMarker = "NOPART";

    /// <summary>
    /// Every routed type of the version: (type name, family key, routing).
    /// Types whose ROUTING section is the canonical empty (<c>ROUTING|-|</c>)
    /// are skipped (not routed).
    /// </summary>
    public static IReadOnlyList<ParsedTypeRouting> Parse(IReadOnlyDictionary<string, string> sectionStrings)
    {
        var result = new List<ParsedTypeRouting>();
        foreach (var pair in sectionStrings)
        {
            if (!pair.Key.StartsWith(RoutingSectionPrefix, StringComparison.Ordinal))
                continue;
            var typeName = pair.Key.Substring(RoutingSectionPrefix.Length);
            var routing = ParseRoutingSection(pair.Value);
            if (routing is null)
                continue;

            var familyKey = string.Empty;
            if (sectionStrings.TryGetValue(FamKeySectionPrefix + typeName, out var famKeySection))
            {
                familyKey = ParseFamKeySection(famKeySection);
            }
            result.Add(new ParsedTypeRouting(typeName, familyKey, routing));
        }
        return result;
    }

    /// <summary>
    /// <c>ROUTING|{preferred}|</c> + per rule
    /// <c>{group}|{part}|{description}|</c> + per criterion
    /// <c>{type}|{min}|{max}|</c>. A rule's criteria are the triples that
    /// follow it until the next group token (int or <c>"Param:"</c>) or the
    /// end of the section. <c>null</c> for the canonical empty section.
    /// </summary>
    private static RoutingPreferencesSnapshot? ParseRoutingSection(string canonical)
    {
        var tokens = canonical.Split('|');
        // ["ROUTING", preferred, ..., ""] — the trailing separator yields an
        // empty last token.
        if (tokens.Length < 3 || tokens[0] != "ROUTING")
            return null;
        if (tokens[1] == "-")
            return null;

        var preferred = int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 0;
        var rules = new List<RoutingRuleSnapshot>();
        var i = 2;
        while (i + 2 < tokens.Length && tokens[i].Length > 0)
        {
            var groupToken = tokens[i];
            int groupType;
            string? groupKey = null;
            if (RoutingGroupKeys.IsParamGroup(groupToken))
            {
                groupType = RoutingGroupKeys.ParamGroupType;
                groupKey = Unescape(groupToken);
            }
            else if (int.TryParse(groupToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var g))
            {
                groupType = g;
            }
            else
            {
                // Not a group token — malformed tail; stop instead of
                // inventing rules (analytics input, never evidence).
                break;
            }

            var partName = Unescape(tokens[i + 1]);
            var description = Unescape(tokens[i + 2]);
            i += 3;

            var criteria = new List<RoutingCriterionSnapshot>();
            while (i + 2 < tokens.Length && tokens[i].Length > 0 && !IsGroupToken(tokens[i]))
            {
                var criterionType = Unescape(tokens[i]);
                var min = ParseDouble(tokens[i + 1]);
                var max = ParseDouble(tokens[i + 2]);
                criteria.Add(new RoutingCriterionSnapshot(criterionType, min, max));
                i += 3;
            }

            rules.Add(new RoutingRuleSnapshot(
                groupType,
                string.Equals(partName, NoPartMarker, StringComparison.Ordinal) ? null : partName,
                description,
                criteria,
                GroupKey: groupKey));
        }

        return new RoutingPreferencesSnapshot(preferred, rules);
    }

    private static bool IsGroupToken(string token)
        => RoutingGroupKeys.IsParamGroup(token)
            || int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

    private static string ParseFamKeySection(string canonical)
    {
        // "FAMKEY|{key}|" — key is escaped content.
        var tokens = canonical.Split('|');
        return tokens.Length >= 2 ? Unescape(tokens[1]) : string.Empty;
    }

    private static double ParseDouble(string token)
        => double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static string Unescape(string value)
    {
        if (value.Length == 0 || value.IndexOf('%') < 0) return value;
        return value.Replace("%7C", "|").Replace("%25", "%");
    }
}

/// <summary>One routed type recovered from stored section strings.</summary>
public sealed record ParsedTypeRouting(
    string TypeName,
    string FamilyKey,
    RoutingPreferencesSnapshot Routing);
