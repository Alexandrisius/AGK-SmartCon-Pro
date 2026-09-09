using System.Globalization;
using System.Text;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Implementation;

/// <summary>
/// Loadable-family canonical sections (Issue #249, Phase 1). Each builder
/// returns the exact substring its section contributes to the full
/// canonical string (marker included), so the concatenation of all
/// sections in canonical order is byte-identical to the pre-refactor
/// monolithic builder — no FHV bump, no migration.
/// </summary>
public sealed partial class FamilyContentHasher
{
    /// <summary>
    /// Build the canonical string for a loadable family snapshot.
    /// Format: FHV18|LOADABLE|{catOrdinal}|PARAMS|...|TYPES|...|PHANTOM|...|DEF|...|GEOM|...|GEOM2D|...|NESTED|...|NONSHARED|...|NESTEDHASH|...|FACTS|...|FLAGS|...|CONN|...|LOOKUP|...
    /// The family name is intentionally NOT part of the hash (v2,
    /// Issue #126): content identity is rename-invariant. The category
    /// is the locale-independent ordinal (v3, Issue #159); the display
    /// name is only a fallback when the ordinal is unknown.
    /// FHV8 (#209, ADR-066): NESTEDHASH — direct shared-nested children
    /// as sorted (escaped name, composite hash hex) pairs, so a nested
    /// content change transitively shifts every ancestor's hash.
    /// FHV10 (owner decision 2026-08-12): parameter GROUPS left the hash —
    /// the only content field a reload merge physically cannot transfer
    /// (probe-proven twice), so versioning them made the embedded
    /// verification fork the hash into two divergent grades. ONE hash now
    /// serves import dedup, versioning, embedded verification and stale
    /// detection alike; a group-only edit no longer version-bumps.
    /// FHV11 (Issue #238): LOOKUP — raw CSV content of embedded lookup
    /// tables (FamilySizeTable), so a table-only edit shifts the hash
    /// instead of producing a false Duplicate. Merge-safe (probe-proven
    /// 2026-08-23): a reload merge transfers lookup tables into the
    /// embedded copy, unlike the groups that killed the two-grade scheme.
    /// The section is OMITTED for table-less families.
    /// FHV12 (Issue #249, Phase 3): DEF — the type-independent definition
    /// wiring (form visibility/material/offset bindings, dimension
    /// labels, reference planes) closes the blind spots no default-type
    /// metric could see; GEOM is strengthened per form (centroid,
    /// face-kind histogram, summed edge lengths, resolved RGBA material,
    /// visibility flags) and gains nested FamilyInstance placements
    /// (NESTEDINST) — moving/rotating a nested part or re-binding a
    /// label previously passed the hash silently. The extractor reads
    /// with IncludeNonVisibleObjects = true (conditionally visible
    /// forms enter the metrics). Per-type hashes (TYPES substrings)
    /// are unaffected — family_type_hashes rows stay valid.
    /// </summary>
    internal static string BuildLoadableCanonicalString(FamilySnapshot snapshot)
    {
        var sections = BuildLoadableSections(snapshot);
        var capacity = 0;
        foreach (var section in sections)
            capacity += section.CanonicalString.Length;

        var sb = new StringBuilder(capacity);
        foreach (var section in sections)
            sb.Append(section.CanonicalString);
        return sb.ToString();
    }

    /// <summary>
    /// Build the ordered canonical sections of a loadable family snapshot.
    /// The LOOKUP section is omitted entirely for table-less families
    /// (FHV11 contract).
    /// </summary>
    internal static IReadOnlyList<ContentSectionHash> BuildLoadableSections(FamilySnapshot snapshot)
    {
        var sections = new List<ContentSectionHash>(14)
        {
            Section(FamilyContentSectionNames.Meta, BuildLoadableMetaSection(snapshot)),
            Section(FamilyContentSectionNames.Params, BuildParamsSection(snapshot)),
            Section(FamilyContentSectionNames.Types, BuildTypesSection(snapshot)),
            Section(FamilyContentSectionNames.Phantom, BuildPhantomSection(snapshot)),
            Section(FamilyContentSectionNames.Def, BuildDefSection(snapshot)),
            Section(FamilyContentSectionNames.Geom, BuildGeomSection(snapshot)),
            Section(FamilyContentSectionNames.Geom2d, BuildGeom2dSection(snapshot)),
            Section(FamilyContentSectionNames.Nested, BuildNestedSection(snapshot)),
            Section(FamilyContentSectionNames.NonShared, BuildNonSharedSection(snapshot)),
            Section(FamilyContentSectionNames.NestedHash, BuildNestedHashSection(snapshot)),
            Section(FamilyContentSectionNames.Facts, BuildFactsSection(snapshot)),
            Section(FamilyContentSectionNames.Flags, BuildFlagsSection(snapshot)),
            Section(FamilyContentSectionNames.Conn, BuildConnSection(snapshot)),
        };

        // FHV11 (Issue #238): LOOKUP is omitted entirely for table-less
        // families so their FHV10→FHV11 churn is limited to the prefix bump.
        if (snapshot.LookupTables is { Count: > 0 })
        {
            sections.Add(Section(FamilyContentSectionNames.Lookup, BuildLookupSection(snapshot)));
        }

        return sections;
    }

    private static ContentSectionHash Section(string name, string canonicalString)
        => new(name, canonicalString, ComputeSha256Hex(canonicalString));

    private static string BuildLoadableMetaSection(FamilySnapshot snapshot)
    {
        var sb = new StringBuilder(32);
        sb.Append("FHV18|LOADABLE|");
        if (snapshot.CategoryId.HasValue)
            sb.Append(snapshot.CategoryId.Value.ToString(CultureInfo.InvariantCulture));
        else
            sb.Append(Escape(snapshot.Category ?? string.Empty));
        sb.Append('|');
        return sb.ToString();
    }

    private static string BuildParamsSection(FamilySnapshot snapshot)
    {
        var sb = new StringBuilder(256);
        sb.Append("PARAMS|");
        var sortedParams = snapshot.Parameters
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.StorageType, StringComparer.Ordinal);
        foreach (var p in sortedParams)
        {
            sb.Append(Escape(p.Name)).Append('|');
            sb.Append(p.StorageType).Append('|');
            // FHV10: the parameter GROUP is deliberately NOT hashed — a
            // reload merge never propagates it (probe-proven), so keeping
            // it would fork identity from embedded verification forever.
            sb.Append(p.IsInstance ? 'I' : 'T').Append('|');
            sb.Append(p.IsShared ? 'S' : 'P').Append('|');
            sb.Append(Escape(p.Formula ?? NullFormulaMarker)).Append('|');
            sb.Append(p.IsDeterminedByFormula ? 'F' : 'N').Append('|');
            sb.Append(p.IsReporting ? 'R' : 'N').Append('|');
            sb.Append(p.SharedParamGuid ?? NullGuidMarker).Append('|');
            sb.Append(p.BuiltInParameterId ?? NullBuiltInMarker).Append('|');
        }
        return sb.ToString();
    }

    private static string BuildTypesSection(FamilySnapshot snapshot)
    {
        var sb = new StringBuilder(256);
        sb.Append("TYPES|");
        var sortedTypes = snapshot.Types
            .OrderBy(t => t.Name, StringComparer.Ordinal);
        foreach (var t in sortedTypes)
        {
            sb.Append(BuildLoadableTypeSubstring(t));
        }
        return sb.ToString();
    }

    /// <summary>
    /// The canonical substring of a single family type inside the TYPES
    /// section: escaped name + ordinal-sorted parameter values. Also the
    /// input of the per-type content hash (Issue #249, Phase 2).
    /// </summary>
    private static string BuildLoadableTypeSubstring(FamilyTypeSnapshot t)
    {
        var sb = new StringBuilder(128);
        sb.Append(Escape(t.Name)).Append('|');
        var sortedValues = t.Values
            .OrderBy(v => v.ParameterName, StringComparer.Ordinal);
        foreach (var v in sortedValues)
        {
            AppendParameterValue(sb, v);
        }
        return sb.ToString();
    }

    /// <summary>
    /// FHV9 (#209 stress test 2026-08-12): parameter values of a
    /// TYPELESS family (phantom default type). Present in the single unified grade:
    /// extraction is context-stable (editor/EditFamily current type vs
    /// raw-open synthesized type read the same defaults — probe
    /// 2026-08-12), and the host cannot drive them (associations live
    /// on instances in the host, never in the embedded document —
    /// DrivenEmbeddedPollutionProbeTests). Without this section an edit
    /// of any non-geometric value on a typeless family (e.g. «Модель»)
    /// never shifted the hash and the import dialog lied «Duplicate».
    /// </summary>
    private static string BuildPhantomSection(FamilySnapshot snapshot)
    {
        var sb = new StringBuilder(64);
        sb.Append("PHANTOM|");
        if (snapshot.PhantomTypeValues is { Count: > 0 } phantomValues)
        {
            foreach (var v in phantomValues.OrderBy(v => v.ParameterName, StringComparer.Ordinal))
            {
                AppendParameterValue(sb, v);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// FHV12 (#249, Phase 3): DEF — type-independent definition wiring.
    /// Per form: visibility/material/extrusion-offset parameter bindings
    /// (+ the offset values); per dimension: label binding + style +
    /// segment count; per reference plane: name + Defines Origin. Entries
    /// are sorted by their FULL canonical content so identical-prefix
    /// forms can never leak the extraction order into the string
    /// (validator H1 lesson). A null <see cref="FamilySnapshot.Definitions"/>
    /// (synthetic/test snapshots) emits the empty section deterministically.
    /// </summary>
    private static string BuildDefSection(FamilySnapshot snapshot)
    {
        var sb = new StringBuilder(128);
        sb.Append("DEF|");
        var def = snapshot.Definitions;
        if (def is not null)
        {
            // FHV14 (#249 follow-up): only forms with at least one parameter
            // BINDING — a binding IS the wiring this section exists to
            // catch. Listing every form (with its offsets) made every "added
            // a 3D body" edit fire DEF; form existence/offsets are measured
            // by the GEOM metrics.
            // FHV17 (#249, manual-test round 5): entries are sorted by their
            // EMITTED canonical string, never by raw doubles — raw offset
            // keys carry sub-quantization regen noise that reorders
            // otherwise identical entries between extractions.
            var boundFormEntries = def.Forms
                .Where(f => f.VisibilityParameterName is not null
                    || f.MaterialParameterName is not null
                    || f.ExtrusionStartParameterName is not null
                    || f.ExtrusionEndParameterName is not null)
                .Select(BuildDefBoundFormEntry)
                .OrderBy(e => e, StringComparer.Ordinal)
                .ToList();
            sb.Append(boundFormEntries.Count).Append('|');
            foreach (var entry in boundFormEntries)
            {
                sb.Append(entry);
            }

            sb.Append("DIMS|");
            // FHV13 (#249 follow-up): only LABELED dimensions — a label IS
            // the wiring this section exists to catch. Unlabeled dimensions
            // (including Revit's automatic sketch dimensions created on
            // every sketch) are not parameter bindings; their geometric
            // effect is measured by the GEOM metrics, and listing them made
            // every "added a 3D body" edit fire the DEF section.
            var sortedDims = def.Dimensions
                .Where(d => d.LabelParameterName is not null)
                .OrderBy(d => d.LabelParameterName, StringComparer.Ordinal)
                .ThenBy(d => d.StyleName, StringComparer.Ordinal)
                .ThenBy(d => d.SegmentCount);
            foreach (var d in sortedDims)
            {
                sb.Append(Escape(d.LabelParameterName ?? AbsentMarker)).Append('|');
                sb.Append(Escape(d.StyleName)).Append('|');
                sb.Append(d.SegmentCount).Append('|');
            }

            sb.Append("PLANES|");
            var sortedPlanes = def.ReferencePlanes
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ThenBy(p => p.DefinesOrigin);
            foreach (var p in sortedPlanes)
            {
                sb.Append(Escape(p.Name)).Append('|');
                sb.Append(FormatFlag(p.DefinesOrigin)).Append('|');
            }
        }
        else
        {
            sb.Append("0|DIMS|PLANES|");
        }
        return sb.ToString();
    }

    private static string BuildNestedSection(FamilySnapshot snapshot)
    {
        var sb = new StringBuilder(64);
        sb.Append("NESTED|");
        var sortedNested = snapshot.SharedNestedFamilyNames
            .OrderBy(n => n, StringComparer.Ordinal);
        foreach (var n in sortedNested)
        {
            sb.Append(Escape(n)).Append('|');
        }
        return sb.ToString();
    }

    private static string BuildNonSharedSection(FamilySnapshot snapshot)
    {
        var sb = new StringBuilder(32);
        sb.Append("NONSHARED|");
        var sortedNonShared = (snapshot.NonSharedNestedFamilyNames ?? (IReadOnlyList<string>)[])
            .OrderBy(n => n, StringComparer.Ordinal);
        foreach (var n in sortedNonShared)
        {
            sb.Append(Escape(n)).Append('|');
        }
        return sb.ToString();
    }

    private static string BuildNestedHashSection(FamilySnapshot snapshot)
    {
        var sb = new StringBuilder(64);
        sb.Append("NESTEDHASH|");
        var sortedNestedHashes = (snapshot.SharedNestedContentHashes ?? (IReadOnlyList<NestedContentHash>)[])
            .OrderBy(e => e.FamilyName, StringComparer.Ordinal);
        foreach (var e in sortedNestedHashes)
        {
            sb.Append(Escape(e.FamilyName)).Append('|');
            sb.Append(e.HashHex).Append('|');
        }
        return sb.ToString();
    }

    private static string BuildFactsSection(FamilySnapshot snapshot)
    {
        var sb = new StringBuilder(32);
        sb.Append("FACTS|");
        var sortedFacts = (snapshot.Facts ?? (IReadOnlyList<FamilyFact>)[])
            .OrderBy(f => f.FactKey, StringComparer.Ordinal);
        foreach (var f in sortedFacts)
        {
            sb.Append(Escape(f.FactKey)).Append('|');
            sb.Append(Escape(f.ValueKey)).Append('|');
        }
        return sb.ToString();
    }

    private static string BuildFlagsSection(FamilySnapshot snapshot)
    {
        var sb = new StringBuilder(24);
        sb.Append("FLAGS|");
        var flags = snapshot.BehaviorFlags;
        sb.Append(FormatFlag(flags?.IsShared)).Append('|');
        sb.Append(FormatFlag(flags?.IsWorkPlaneBased)).Append('|');
        sb.Append(FormatFlag(flags?.IsAlwaysVertical)).Append('|');
        sb.Append(FormatFlag(flags?.AllowsCutWithVoids)).Append('|');
        return sb.ToString();
    }

    private static string BuildConnSection(FamilySnapshot snapshot)
    {
        var sb = new StringBuilder(128);
        sb.Append("CONN|");
        // FHV17 (#249, manual-test round 5): connectors sort by their full
        // emitted entry — the pre-FHV17 key (domain/shape/class/linked
        // index) tied for two identical unlinked connectors and leaked the
        // extraction order.
        var connectorEntries = (snapshot.Connectors ?? (IReadOnlyList<ConnectorSnapshot>)[])
            .Select(BuildConnectorEntry)
            .OrderBy(e => e, StringComparer.Ordinal);
        foreach (var entry in connectorEntries)
        {
            sb.Append(entry);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Full canonical entry of one connector (ends with the '|' separator);
    /// doubles as its own sort key (FHV17).
    /// </summary>
    private static string BuildConnectorEntry(ConnectorSnapshot c)
    {
        var sb = new StringBuilder(64);
        sb.Append(c.Domain).Append('|');
        sb.Append(c.Shape).Append('|');
        sb.Append(c.SystemClassification).Append('|');
        sb.Append(c.IsPrimary ? 'P' : '-').Append('|');
        sb.Append(FormatSize(c.Width)).Append('|');
        sb.Append(FormatSize(c.Height)).Append('|');
        sb.Append(FormatSize(c.Radius)).Append('|');
        sb.Append(FormatCoord(c.OriginX)).Append('|');
        sb.Append(FormatCoord(c.OriginY)).Append('|');
        sb.Append(FormatCoord(c.OriginZ)).Append('|');
        sb.Append(c.LinkedIndex).Append('|');
        return sb.ToString();
    }

    /// <summary>
    /// FHV11 (Issue #238): LOOKUP — raw CSV content of the family's
    /// embedded lookup tables (таблицы поиска, FamilySizeTable). Omitted
    /// entirely for table-less families so their FHV10→FHV11 churn is
    /// limited to the prefix bump. Tables sorted by name (Ordinal) —
    /// multiple tables per family are normal for MEP fittings.
    /// </summary>
    private static string BuildLookupSection(FamilySnapshot snapshot)
    {
        var sb = new StringBuilder(128);
        sb.Append("LOOKUP|");
        foreach (var table in snapshot.LookupTables!.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            sb.Append(Escape(table.Name)).Append('|');
            sb.Append(Escape(table.CsvContent)).Append('|');
        }
        return sb.ToString();
    }
}
