using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Implementation;

/// <summary>
/// Default classification of a version-vs-version content change
/// (Issue #249, Phase 4; owner-approved defaults 2026-08-27):
/// <list type="bullet">
/// <item><b>major:</b> DEF, GEOM, CONN, STRUCT, ROUTING — the physical
/// shape, wiring or MEP behavior changed;</item>
/// <item><b>minor:</b> TYPES/VALUES (type values), PHANTOM, NESTED,
/// NONSHARED, NESTEDHASH, LOOKUP, FLAGS, PARAMS — schema, values and
/// references, reviewable at a glance;</item>
/// <item><b>trivial:</b> everything else (GEOM2D, FACTS, META, …).</item>
/// </list>
/// Section keys may be flattened per-type keys (<c>"STRUCT|{TypeName}"</c>)
/// — classification always applies to the BASE section name.
/// </summary>
public static class ContentChangeClassifier
{
    private static readonly HashSet<string> MajorSections = new(StringComparer.Ordinal)
    {
        FamilyContentSectionNames.Def,
        FamilyContentSectionNames.Geom,
        FamilyContentSectionNames.Conn,
        FamilyContentSectionNames.Struct,
        FamilyContentSectionNames.Routing,
    };

    private static readonly HashSet<string> MinorSections = new(StringComparer.Ordinal)
    {
        FamilyContentSectionNames.Types,
        FamilyContentSectionNames.Values,
        FamilyContentSectionNames.Phantom,
        FamilyContentSectionNames.Nested,
        FamilyContentSectionNames.NonShared,
        FamilyContentSectionNames.NestedHash,
        FamilyContentSectionNames.Lookup,
        FamilyContentSectionNames.Flags,
        FamilyContentSectionNames.Params,
    };

    /// <summary>
    /// Classify a change by the set of differing section keys. The
    /// strongest class wins (any major section → Major; otherwise any
    /// minor → Minor; otherwise Trivial when anything changed, None when
    /// nothing did).
    /// </summary>
    public static ContentChangeClass Classify(IEnumerable<string> changedSectionKeys)
    {
        var result = ContentChangeClass.None;
        foreach (var key in changedSectionKeys)
        {
            var baseName = BaseSectionName(key);
            if (MajorSections.Contains(baseName))
            {
                return ContentChangeClass.Major;
            }
            if (MinorSections.Contains(baseName))
            {
                result = ContentChangeClass.Minor;
            }
            else if (result == ContentChangeClass.None)
            {
                result = ContentChangeClass.Trivial;
            }
        }
        return result;
    }

    /// <summary>
    /// Base section name of a (possibly per-type flattened) section key:
    /// <c>"STRUCT"</c> for <c>"STRUCT"</c> and <c>"STRUCT|{TypeName}"</c>.
    /// Section names never contain '|', so the first segment is always
    /// the section.
    /// </summary>
    public static string BaseSectionName(string sectionKey)
    {
        var pipeIndex = sectionKey.IndexOf('|');
        return pipeIndex < 0 ? sectionKey : sectionKey[..pipeIndex];
    }
}
