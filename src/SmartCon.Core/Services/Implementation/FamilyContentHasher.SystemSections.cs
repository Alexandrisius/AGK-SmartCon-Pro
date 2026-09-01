using System.Globalization;
using System.Text;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Implementation;

/// <summary>
/// System-family canonical sections (Issue #249, Phase 1). The FHV7
/// canonical string interleaves per-type bodies (VALUES + FAMKEY + STRUCT
/// + … + WIRE inside the per-type loop), so the faithful decomposition is:
/// one META section (format prefix + the <c>TYPES|</c> marker) followed by
/// per-type section entries carrying <see cref="ContentSectionHash.TypeName"/>.
/// Concatenation in emission order reproduces the pre-refactor string
/// byte-for-byte — no FHV bump, no migration. The per-type body is also
/// the input of the system per-type content hash (#179, Phase 2).
/// </summary>
public sealed partial class FamilyContentHasher
{
    /// <summary>
    /// Build the canonical string for a system family snapshot.
    /// Format: FHV8|SYSTEM|{catId}|TYPES|{typeName}|{params}|FAMKEY|...|STRUCT|...|ROUTING|...|SEGMENTS|...|SUBTYPES|...|RAILING|...|WIRE|...
    /// The category display name is NOT part of the hash (v3, Issue #159):
    /// it is UI-locale dependent — the ordinal is the identity.
    /// STRUCT/ROUTING/SEGMENTS/SUBTYPES/RAILING/WIRE live inside the per-type
    /// loop (they are per-type data). FHV4 (ADR-065): +FAMKEY (locale-
    /// invariant family identity, #190), STRUCT gains StructuralMaterialIndex/
    /// EndCap/OpeningWrapping and per-layer LayerCapFlag/ParticipatesInWrapping
    /// (#179), new SEGMENTS (segment size tables), SUBTYPES (stairs subtype
    /// references by name, #184) and RAILING (railing structure summary).
    /// FHV5: WIRE — the wire settings graph (material/temperature rating/
    /// insulation/max size/conduit + neutral scalars), which lives on
    /// WireType API properties and never appears in Element.Parameters
    /// (manual test 2026-08-04).
    /// FHV6 (stress test 2026-08-05): TYPES ordering gains deterministic
    /// tie-breaks — OrderBy is a STABLE sort, so same-named types of
    /// different families (both conduits are «Короб») previously kept the
    /// extraction order, which differs between the source project and the
    /// staged mini-project → false "Существующая" instead of "Дубликат".
    /// FHV7 (#215): FAMKEY gains the duct Shape discriminator
    /// (Duct.Round/Rectangular/Oval instead of "Single") — the
    /// "Воздуховоды" category has three system families, not one.
    /// FHV8 (#249, Phase 3): prefix bump only (content unchanged) —
    /// aligned with the loadable FHV12 so a single global
    /// hash_format_version (=12) covers both kinds; per-type content
    /// hashes ride along as a side product (#179).
    /// FHV9 (#254, ADR-072): parameter-based routing enters ROUTING —
    /// flex/conduit/cable-tray types have no RoutingPreferenceManager,
    /// their fitting selection lives in visible built-in parameters
    /// (RBS_CURVETYPE_*); those leave VALUES and become ROUTING rules
    /// with string group keys ("Param:<BIP>"). Pipe/duct tokens are
    /// byte-identical (their routing bips are hidden from
    /// Element.Parameters).
    /// FHV10 (owner decision 2026-08-29, ADR-072 World B): the ROUTING
    /// section leaves the content hash entirely — routing preferences
    /// are links between catalog families, not file content: editor
    /// edits must not version-bump, and a re-import must not overwrite
    /// curated catalog links. The parameter-based BIPs stay OUT of
    /// VALUES (their ElementId tokens reference project fittings — the
    /// #254 phantom-diff class); routing equality moves to the separate
    /// <see cref="RoutingFingerprint"/> used by sync/stale/placement.
    /// System META prefix FHV9→FHV10. Critical task <c>hash-v20</c>
    /// recomputes every row that is not current.
    /// </summary>
    internal static string BuildSystemCanonicalString(SystemFamilySnapshot snapshot)
    {
        var sections = BuildSystemSections(snapshot);
        var capacity = 0;
        foreach (var section in sections)
            capacity += section.CanonicalString.Length;

        var sb = new StringBuilder(capacity);
        foreach (var section in sections)
            sb.Append(section.CanonicalString);
        return sb.ToString();
    }

    /// <summary>
    /// Build the ordered canonical sections of a system family snapshot:
    /// the META prefix (with the <c>TYPES|</c> marker) followed by the
    /// per-type entries (VALUES, FAMKEY, STRUCT, ROUTING, SEGMENTS,
    /// SUBTYPES, RAILING, WIRE) in canonical type order.
    /// </summary>
    internal static IReadOnlyList<ContentSectionHash> BuildSystemSections(SystemFamilySnapshot snapshot)
    {
        var sortedTypes = SortSystemTypes(snapshot.Types);
        var meta = BuildSystemMetaSection(snapshot);
        var sections = new List<ContentSectionHash>(1 + sortedTypes.Count * 8)
        {
            new(
                FamilyContentSectionNames.Meta,
                meta,
                ComputeSha256Hex(meta)),
        };

        foreach (var t in sortedTypes)
        {
            Add(sections, FamilyContentSectionNames.Values, BuildSystemTypeValuesSubstring(t), t.Name);
            Add(sections, FamilyContentSectionNames.FamKey, BuildFamKeySubstring(t), t.Name);
            Add(sections, FamilyContentSectionNames.Struct, BuildStructSubstring(t), t.Name);
            Add(sections, FamilyContentSectionNames.Segments, BuildSegmentsSubstring(t), t.Name);
            Add(sections, FamilyContentSectionNames.Subtypes, BuildSubtypesSubstring(t), t.Name);
            Add(sections, FamilyContentSectionNames.Railing, BuildRailingSubstring(t), t.Name);
            Add(sections, FamilyContentSectionNames.Wire, BuildWireSubstring(t), t.Name);
        }

        return sections;

        static void Add(List<ContentSectionHash> list, string name, string canonicalString, string typeName)
            => list.Add(new ContentSectionHash(name, canonicalString, ComputeSha256Hex(canonicalString), typeName));
    }

    private static List<SystemTypeSnapshot> SortSystemTypes(IReadOnlyList<SystemTypeSnapshot> types)
    {
        return types
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ThenBy(t => t.FamilyKey ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(t => t.FamilyName ?? string.Empty, StringComparer.Ordinal)
            .ToList();
    }

    private static string BuildSystemMetaSection(SystemFamilySnapshot snapshot)
    {
        var sb = new StringBuilder(32);
        sb.Append("FHV11|SYSTEM|");
        sb.Append(snapshot.CategoryId).Append('|');
        sb.Append("TYPES|");
        return sb.ToString();
    }

    /// <summary>
    /// The full canonical body of a single system type: name + values +
    /// FAMKEY + STRUCT + ROUTING + SEGMENTS + SUBTYPES + RAILING + WIRE.
    /// Input of the system per-type content hash (#179, Issue #249 Phase 2).
    /// </summary>
    private static string BuildSystemTypeBody(SystemTypeSnapshot t)
    {
        var sb = new StringBuilder(256);
        sb.Append(BuildSystemTypeValuesSubstring(t));
        sb.Append(BuildFamKeySubstring(t));
        sb.Append(BuildStructSubstring(t));
        sb.Append(BuildSegmentsSubstring(t));
        sb.Append(BuildSubtypesSubstring(t));
        sb.Append(BuildRailingSubstring(t));
        sb.Append(BuildWireSubstring(t));
        return sb.ToString();
    }

    private static string BuildSystemTypeValuesSubstring(SystemTypeSnapshot t)
    {
        var sb = new StringBuilder(128);
        sb.Append(Escape(t.Name)).Append('|');
        var sortedValues = t.Values
            .OrderBy(v => v.ParameterName, StringComparer.Ordinal);
        foreach (var v in sortedValues)
        {
            if (IsBlankValue(v.HasValue, v.ValueText, v.StorageType)) continue;
            if (IsAutoGeneratedParameter(v.ParameterName)) continue;
            sb.Append(Escape(v.ParameterName)).Append('|');
            sb.Append(v.StorageType).Append('|');
            sb.Append('V').Append('|');
            if (v.ValueNumber.HasValue)
                sb.Append(v.ValueNumber.Value.ToString("0.######", CultureInfo.InvariantCulture));
            else
                sb.Append(Escape(v.ValueText ?? string.Empty));
            sb.Append('|');
            sb.Append(Escape(v.ResolvedElementName ?? NullElementMarker)).Append('|');
        }
        return sb.ToString();
    }

    private static string BuildFamKeySubstring(SystemTypeSnapshot t)
    {
        var sb = new StringBuilder(32);
        sb.Append("FAMKEY|");
        sb.Append(Escape(t.FamilyKey ?? string.Empty)).Append('|');
        return sb.ToString();
    }

    private static string BuildStructSubstring(SystemTypeSnapshot t)
    {
        var sb = new StringBuilder(96);
        sb.Append("STRUCT|");
        if (t.Structure is not null)
        {
            sb.Append(t.Structure.ExteriorShellLayerCount).Append('|');
            sb.Append(t.Structure.InteriorShellLayerCount).Append('|');
            sb.Append(t.Structure.StructuralMaterialIndex).Append('|');
            sb.Append(t.Structure.EndCap).Append('|');
            sb.Append(t.Structure.OpeningWrapping).Append('|');
            foreach (var layer in t.Structure.Layers)
            {
                sb.Append(layer.Function).Append('|');
                sb.Append(layer.Width.ToString("0.######", CultureInfo.InvariantCulture)).Append('|');
                sb.Append(Escape(layer.MaterialName ?? NullMaterialMarker)).Append('|');
                sb.Append(layer.IsVariable ? 'V' : 'N').Append('|');
                sb.Append(layer.LayerCapFlag ? 'C' : 'N').Append('|');
                sb.Append(layer.ParticipatesInWrapping ? 'W' : 'N').Append('|');
            }
        }
        else
        {
            sb.Append('-').Append('|');
        }
        return sb.ToString();
    }

    private static string BuildSegmentsSubstring(SystemTypeSnapshot t)
    {
        var sb = new StringBuilder(64);
        sb.Append("SEGMENTS|");
        if (t.Segments is not null)
        {
            foreach (var segment in t.Segments)
            {
                sb.Append(Escape(segment.Name)).Append('|');
                sb.Append(Escape(segment.MaterialName ?? NullMaterialMarker)).Append('|');
                sb.Append(Escape(segment.ScheduleName ?? NullPartMarker)).Append('|');
                sb.Append(segment.Roughness.ToString("0.######", CultureInfo.InvariantCulture)).Append('|');
                // FHV21 (owner decision 2026-09-01): the routing rule's
                // size-range criterion is mini-owned segment configuration —
                // editing Мин/Макс in the mini changes the hash and forks a
                // new version. '-' = unrestricted (no criterion on the rule).
                sb.Append(segment.RuleMinSizeFeet?.ToString("0.######", CultureInfo.InvariantCulture) ?? "-").Append('|');
                sb.Append(segment.RuleMaxSizeFeet?.ToString("0.######", CultureInfo.InvariantCulture) ?? "-").Append('|');
                foreach (var size in segment.Sizes)
                {
                    sb.Append(size.NominalDiameter.ToString("0.######", CultureInfo.InvariantCulture)).Append('|');
                    sb.Append(size.InnerDiameter.ToString("0.######", CultureInfo.InvariantCulture)).Append('|');
                    sb.Append(size.OuterDiameter.ToString("0.######", CultureInfo.InvariantCulture)).Append('|');
                    sb.Append(size.UsedInSizeLists ? 'L' : 'N').Append('|');
                    sb.Append(size.UsedInSizing ? 'S' : 'N').Append('|');
                }
            }
        }
        else
        {
            sb.Append('-').Append('|');
        }
        return sb.ToString();
    }

    private static string BuildSubtypesSubstring(SystemTypeSnapshot t)
    {
        var sb = new StringBuilder(48);
        sb.Append("SUBTYPES|");
        if (t.Stairs is not null)
        {
            sb.Append(Escape(t.Stairs.RunTypeName ?? NullPartMarker)).Append('|');
            sb.Append(Escape(t.Stairs.LandingTypeName ?? NullPartMarker)).Append('|');
            sb.Append(Escape(t.Stairs.LeftSupportTypeName ?? NullPartMarker)).Append('|');
            sb.Append(Escape(t.Stairs.RightSupportTypeName ?? NullPartMarker)).Append('|');
            sb.Append(Escape(t.Stairs.MiddleSupportTypeName ?? NullPartMarker)).Append('|');
            sb.Append(Escape(t.Stairs.CutMarkTypeName ?? NullPartMarker)).Append('|');
        }
        else
        {
            sb.Append('-').Append('|');
        }
        return sb.ToString();
    }

    private static string BuildRailingSubstring(SystemTypeSnapshot t)
    {
        var sb = new StringBuilder(96);
        sb.Append("RAILING|");
        if (t.Railing is not null)
        {
            var r = t.Railing;
            sb.Append(Escape(r.TopRailTypeName ?? NullPartMarker)).Append('|');
            AppendNullableNumber(sb, r.TopRailHeight);
            sb.Append(Escape(r.PrimaryHandrailTypeName ?? NullPartMarker)).Append('|');
            AppendNullableNumber(sb, r.PrimaryHandrailHeight);
            AppendNullableNumber(sb, r.PrimaryHandrailLateralOffset);
            AppendNullableInt(sb, r.PrimaryHandrailPosition);
            sb.Append(Escape(r.SecondaryHandrailTypeName ?? NullPartMarker)).Append('|');
            AppendNullableNumber(sb, r.SecondaryHandrailHeight);
            AppendNullableNumber(sb, r.SecondaryHandrailLateralOffset);
            AppendNullableInt(sb, r.SecondaryHandrailPosition);
            foreach (var rail in r.Rails)
            {
                sb.Append(Escape(rail.Name)).Append('|');
                sb.Append(rail.Height.ToString("0.######", CultureInfo.InvariantCulture)).Append('|');
                sb.Append(rail.Offset.ToString("0.######", CultureInfo.InvariantCulture)).Append('|');
                sb.Append(Escape(rail.ProfileName ?? NullPartMarker)).Append('|');
                sb.Append(Escape(rail.MaterialName ?? NullMaterialMarker)).Append('|');
            }
            var b = r.Balusters;
            sb.Append(b.PatternLength.ToString("0.######", CultureInfo.InvariantCulture)).Append('|');
            sb.Append(b.DistributionJustification).Append('|');
            sb.Append(b.BreakPattern).Append('|');
            foreach (var balusterName in b.BalusterFamilyNames)
            {
                sb.Append(Escape(balusterName ?? NullPartMarker)).Append('|');
            }
            sb.Append(b.UseBalusterPerTreadOnStairs ? 'T' : 'N').Append('|');
            sb.Append(b.BalusterPerTreadNumber).Append('|');
            sb.Append(Escape(b.BalusterPerTreadFamilyName ?? NullPartMarker)).Append('|');
        }
        else
        {
            sb.Append('-').Append('|');
        }
        return sb.ToString();
    }

    private static string BuildWireSubstring(SystemTypeSnapshot t)
    {
        var sb = new StringBuilder(48);
        sb.Append("WIRE|");
        if (t.Wire is not null)
        {
            var w = t.Wire;
            sb.Append(Escape(w.MaterialName ?? NullPartMarker)).Append('|');
            sb.Append(Escape(w.TemperatureRatingName ?? NullPartMarker)).Append('|');
            sb.Append(Escape(w.InsulationName ?? NullPartMarker)).Append('|');
            sb.Append(Escape(w.MaxSizeName ?? NullPartMarker)).Append('|');
            sb.Append(Escape(w.ConduitName ?? NullPartMarker)).Append('|');
            AppendNullableNumber(sb, w.NeutralMultiplier);
            if (w.NeutralRequired.HasValue)
                sb.Append(w.NeutralRequired.Value ? 'T' : 'N').Append('|');
            else
                sb.Append(NullPartMarker).Append('|');
        }
        else
        {
            sb.Append('-').Append('|');
        }
        return sb.ToString();
    }
}
