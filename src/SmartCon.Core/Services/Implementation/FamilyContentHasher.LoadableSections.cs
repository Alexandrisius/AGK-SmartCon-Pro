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
    /// Format: FHV11|LOADABLE|{catOrdinal}|PARAMS|...|TYPES|...|PHANTOM|...|GEOM|...|GEOM2D|...|NESTED|...|NONSHARED|...|NESTEDHASH|...|FACTS|...|FLAGS|...|CONN|...|LOOKUP|...
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
        var sections = new List<ContentSectionHash>(13)
        {
            Section(FamilyContentSectionNames.Meta, BuildLoadableMetaSection(snapshot)),
            Section(FamilyContentSectionNames.Params, BuildParamsSection(snapshot)),
            Section(FamilyContentSectionNames.Types, BuildTypesSection(snapshot)),
            Section(FamilyContentSectionNames.Phantom, BuildPhantomSection(snapshot)),
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
        sb.Append("FHV11|LOADABLE|");
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

    private static string BuildGeomSection(FamilySnapshot snapshot)
    {
        var sb = new StringBuilder(256);
        sb.Append("GEOM|");
        sb.Append(snapshot.Geometry.TotalFormCount).Append('|');
        // Deterministic topology-field sort (validator H1): identical
        // ordering across extraction contexts (raw file vs EditFamily
        // copy) even when metric tie-breaks would be ambiguous.
        var sortedForms = snapshot.Geometry.Forms
            .OrderBy(f => f.FormKind, StringComparer.Ordinal)
            .ThenBy(f => f.IsSolid)
            .ThenBy(f => f.FaceCount)
            .ThenBy(f => f.EdgeCount)
            .ThenBy(f => f.SubcategoryName, StringComparer.Ordinal);
        foreach (var f in sortedForms)
        {
            sb.Append(f.FormKind).Append('|');
            sb.Append(f.IsSolid ? 'S' : 'V').Append('|');
            sb.Append(f.Volume.ToString("0.######", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(f.FaceCount).Append('|');
            sb.Append(f.EdgeCount).Append('|');
            sb.Append(Escape(f.SubcategoryName ?? NullSubcatMarker)).Append('|');
            sb.Append(f.SurfaceArea.ToString("0.######", CultureInfo.InvariantCulture)).Append('|');
            if (f.Bounds is not null)
            {
                sb.Append(FormatCoord(f.Bounds.MinX)).Append(',');
                sb.Append(FormatCoord(f.Bounds.MinY)).Append(',');
                sb.Append(FormatCoord(f.Bounds.MinZ)).Append(',');
                sb.Append(FormatCoord(f.Bounds.MaxX)).Append(',');
                sb.Append(FormatCoord(f.Bounds.MaxY)).Append(',');
                sb.Append(FormatCoord(f.Bounds.MaxZ));
            }
            else
            {
                sb.Append('-');
            }
            sb.Append('|');
        }
        return sb.ToString();
    }

    private static string BuildGeom2dSection(FamilySnapshot snapshot)
    {
        var sb = new StringBuilder(96);
        sb.Append("GEOM2D|");
        sb.Append(snapshot.Geometry.SymbolicCurveCount).Append('|');
        sb.Append(snapshot.Geometry.DetailCurveCount).Append('|');
        sb.Append(snapshot.Geometry.ModelCurveCount).Append('|');
        sb.Append(snapshot.Geometry.TextNoteCount).Append('|');
        sb.Append(snapshot.Geometry.ReferencePlaneCount).Append('|');
        sb.Append(snapshot.Geometry.DimensionCount).Append('|');
        sb.Append(snapshot.Geometry.TotalSymbolicCurveLength.ToString("0.######", CultureInfo.InvariantCulture)).Append('|');
        sb.Append(snapshot.Geometry.TotalDetailCurveLength.ToString("0.######", CultureInfo.InvariantCulture)).Append('|');
        sb.Append(snapshot.Geometry.TotalModelCurveLength.ToString("0.######", CultureInfo.InvariantCulture)).Append('|');
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
        var sortedConnectors = (snapshot.Connectors ?? (IReadOnlyList<ConnectorSnapshot>)[])
            .OrderBy(c => c.Domain)
            .ThenBy(c => c.Shape)
            .ThenBy(c => c.SystemClassification)
            .ThenBy(c => c.LinkedIndex);
        foreach (var c in sortedConnectors)
        {
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
        }
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
