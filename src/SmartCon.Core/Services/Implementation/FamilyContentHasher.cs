using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Core.Services.Implementation;

/// <summary>
/// Pure C# content-hash computation from <see cref="FamilySnapshot"/> and
/// <see cref="SystemFamilySnapshot"/>. Builds a deterministic canonical
/// string (sorted, invariant culture, format-version prefixed) and hashes
/// it with SHA-256. No Revit API calls — entirely deterministic and
/// stable across SaveAs, rename, Revit upgrade and UI locale.
/// </summary>
/// <remarks>
/// FHV3 (ADR-056, Issue #159): category ordinal replaces the
/// locale-dependent display name; new sections FACTS (Part Type), FLAGS
/// (behavior flags), CONN (connectors), STRUCT/ROUTING for system types;
/// geometry gains bounding box + surface area; string values are escaped
/// (<c>%</c> → <c>%25</c>, <c>|</c> → <c>%7C</c>) so the field separator
/// cannot collide with content.
/// </remarks>
public sealed class FamilyContentHasher : IFamilyContentHasher
{
    private const string NullElementMarker = "NULLELEMENT";
    private const string NullFormulaMarker = "NOFORMULA";
    private const string NullGuidMarker = "NOGUID";
    private const string NullBuiltInMarker = "NOBUILTIN";
    private const string NullSubcatMarker = "NOSUBCAT";
    private const string NullMaterialMarker = "NOMATERIAL";
    private const string NullPartMarker = "NOPART";
    private const string AbsentMarker = "-";

    public FamilyContentHash? ComputeForLoadable(FamilySnapshot snapshot)
    {
        if (snapshot is null)
            return null;

        var canonical = BuildLoadableCanonicalString(snapshot);
        var hex = ComputeSha256Hex(canonical);

        using var _logScope = SmartConLogger.BeginScope("ContentHash",
            ("Method", nameof(ComputeForLoadable)),
            ("Family", snapshot.FamilyName),
            ("ParamCount", snapshot.Parameters.Count),
            ("TypeCount", snapshot.Types.Count),
            ("FormCount", snapshot.Geometry.TotalFormCount),
            ("ConnectorCount", snapshot.Connectors?.Count ?? 0),
            ("Hash", hex));
        SmartConLogger.Info($"Loadable hash computed ({snapshot.Parameters.Count} params, {snapshot.Types.Count} types, {snapshot.Geometry.TotalFormCount} forms, {snapshot.Connectors?.Count ?? 0} connectors)");
        var preview = canonical.Length > 200
            ? canonical[..200] + "…[truncated]"
            : canonical;
        SmartConLogger.Debug($"Canonical string (len={canonical.Length}): {preview}");

        return new FamilyContentHash(
            HexString: hex,
            FormatVersion: FamilyContentHashFormat.CurrentVersion,
            SourceKind: "loadable");
    }

    public FamilyContentHash? ComputeForSystem(SystemFamilySnapshot snapshot)
    {
        if (snapshot is null)
            return null;

        if (snapshot.Types.Count == 0)
        {
            using var _scope = SmartConLogger.BeginScope("ContentHash",
                ("Method", nameof(ComputeForSystem)),
                ("Category", snapshot.CategoryName),
                ("Result", "EmptySnapshot"));
            SmartConLogger.Warn("System snapshot has no types — hash not computed. [Action: check that types are selected for this category]");
            return null;
        }

        var canonical = BuildSystemCanonicalString(snapshot);
        var hex = ComputeSha256Hex(canonical);

        using var _logScope = SmartConLogger.BeginScope("ContentHash",
            ("Method", nameof(ComputeForSystem)),
            ("Category", snapshot.CategoryName),
            ("TypeCount", snapshot.Types.Count),
            ("Hash", hex));
        SmartConLogger.Info($"System hash computed ({snapshot.CategoryName}, {snapshot.Types.Count} types)");
        var preview = canonical.Length > 200
            ? canonical[..200] + "…[truncated]"
            : canonical;
        SmartConLogger.Debug($"Canonical string (len={canonical.Length}): {preview}");

        return new FamilyContentHash(
            HexString: hex,
            FormatVersion: FamilyContentHashFormat.CurrentVersion,
            SourceKind: "system");
    }

    /// <summary>
    /// Build the canonical string for a loadable family snapshot.
    /// Format: FHV3|LOADABLE|{catOrdinal}|PARAMS|...|TYPES|...|GEOM|...|GEOM2D|...|NESTED|...|FACTS|...|FLAGS|...|CONN|...
    /// The family name is intentionally NOT part of the hash (v2,
    /// Issue #126): content identity is rename-invariant. The category
    /// is the locale-independent ordinal (v3, Issue #159); the display
    /// name is only a fallback when the ordinal is unknown.
    /// </summary>
    internal static string BuildLoadableCanonicalString(FamilySnapshot snapshot)
    {
        var sb = new StringBuilder(768);
        sb.Append("FHV3|LOADABLE|");
        if (snapshot.CategoryId.HasValue)
            sb.Append(snapshot.CategoryId.Value.ToString(CultureInfo.InvariantCulture));
        else
            sb.Append(Escape(snapshot.Category ?? string.Empty));
        sb.Append('|');

        sb.Append("PARAMS|");
        var sortedParams = snapshot.Parameters
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.StorageType, StringComparer.Ordinal);
        foreach (var p in sortedParams)
        {
            sb.Append(Escape(p.Name)).Append('|');
            sb.Append(p.StorageType).Append('|');
            sb.Append(Escape(p.ParameterGroup ?? string.Empty)).Append('|');
            sb.Append(p.IsInstance ? 'I' : 'T').Append('|');
            sb.Append(p.IsShared ? 'S' : 'P').Append('|');
            sb.Append(Escape(p.Formula ?? NullFormulaMarker)).Append('|');
            sb.Append(p.IsDeterminedByFormula ? 'F' : 'N').Append('|');
            sb.Append(p.IsReporting ? 'R' : 'N').Append('|');
            sb.Append(p.SharedParamGuid ?? NullGuidMarker).Append('|');
            sb.Append(p.BuiltInParameterId ?? NullBuiltInMarker).Append('|');
        }

        sb.Append("TYPES|");
        var sortedTypes = snapshot.Types
            .OrderBy(t => t.Name, StringComparer.Ordinal);
        foreach (var t in sortedTypes)
        {
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
        }

        sb.Append("GEOM|");
        sb.Append(snapshot.Geometry.TotalFormCount).Append('|');
        var sortedForms = snapshot.Geometry.Forms
            .OrderBy(f => f.FormKind, StringComparer.Ordinal)
            .ThenBy(f => f.IsSolid)
            .ThenBy(f => f.Volume);
        foreach (var f in sortedForms)
        {
            sb.Append(f.FormKind).Append('|');
            sb.Append(f.IsSolid ? "S" : "V").Append('|');
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

        sb.Append("NESTED|");
        var sortedNested = snapshot.SharedNestedFamilyNames
            .OrderBy(n => n, StringComparer.Ordinal);
        foreach (var n in sortedNested)
        {
            sb.Append(Escape(n)).Append('|');
        }
        sb.Append("NONSHARED|");
        var sortedNonShared = (snapshot.NonSharedNestedFamilyNames ?? (IReadOnlyList<string>)[])
            .OrderBy(n => n, StringComparer.Ordinal);
        foreach (var n in sortedNonShared)
        {
            sb.Append(Escape(n)).Append('|');
        }

        sb.Append("FACTS|");
        var sortedFacts = (snapshot.Facts ?? (IReadOnlyList<FamilyFact>)[])
            .OrderBy(f => f.FactKey, StringComparer.Ordinal);
        foreach (var f in sortedFacts)
        {
            sb.Append(Escape(f.FactKey)).Append('|');
            sb.Append(Escape(f.ValueKey)).Append('|');
        }

        sb.Append("FLAGS|");
        var flags = snapshot.BehaviorFlags;
        sb.Append(FormatFlag(flags?.IsShared)).Append('|');
        sb.Append(FormatFlag(flags?.IsWorkPlaneBased)).Append('|');
        sb.Append(FormatFlag(flags?.IsAlwaysVertical)).Append('|');
        sb.Append(FormatFlag(flags?.AllowsCutWithVoids)).Append('|');

        sb.Append("CONN|");
        var sortedConnectors = (snapshot.Connectors ?? (IReadOnlyList<ConnectorSnapshot>)[])
            .OrderBy(c => c.Domain)
            .ThenBy(c => c.Shape)
            .ThenBy(c => c.SystemClassification)
            .ThenBy(c => c.OriginX)
            .ThenBy(c => c.OriginY)
            .ThenBy(c => c.OriginZ);
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
    /// Build the canonical string for a system family snapshot.
    /// Format: FHV5|SYSTEM|{catId}|TYPES|{typeName}|{params}|FAMKEY|...|STRUCT|...|ROUTING|...|SEGMENTS|...|SUBTYPES|...|RAILING|...|WIRE|...
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
    /// </summary>
    internal static string BuildSystemCanonicalString(SystemFamilySnapshot snapshot)
    {
        var sb = new StringBuilder(512);
        sb.Append("FHV5|SYSTEM|");
        sb.Append(snapshot.CategoryId).Append('|');

        sb.Append("TYPES|");
        var sortedTypes = snapshot.Types
            .OrderBy(t => t.Name, StringComparer.Ordinal);
        foreach (var t in sortedTypes)
        {
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

            sb.Append("FAMKEY|");
            sb.Append(Escape(t.FamilyKey ?? string.Empty)).Append('|');

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

            sb.Append("ROUTING|");
            if (t.Routing is not null)
            {
                sb.Append(t.Routing.PreferredJunctionType).Append('|');
                foreach (var rule in t.Routing.Rules)
                {
                    sb.Append(rule.GroupType).Append('|');
                    sb.Append(Escape(rule.PartName ?? NullPartMarker)).Append('|');
                    sb.Append(Escape(rule.Description)).Append('|');
                    foreach (var criterion in rule.Criteria)
                    {
                        sb.Append(Escape(criterion.CriterionType)).Append('|');
                        sb.Append(criterion.MinimumSize.ToString("0.######", CultureInfo.InvariantCulture)).Append('|');
                        sb.Append(criterion.MaximumSize.ToString("0.######", CultureInfo.InvariantCulture)).Append('|');
                    }
                }
            }
            else
            {
                sb.Append('-').Append('|');
            }

            sb.Append("SEGMENTS|");
            if (t.Segments is not null)
            {
                foreach (var segment in t.Segments)
                {
                    sb.Append(Escape(segment.Name)).Append('|');
                    sb.Append(Escape(segment.MaterialName ?? NullMaterialMarker)).Append('|');
                    sb.Append(Escape(segment.ScheduleName ?? NullPartMarker)).Append('|');
                    sb.Append(segment.Roughness.ToString("0.######", CultureInfo.InvariantCulture)).Append('|');
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
        }

        return sb.ToString();
    }

    private static void AppendNullableNumber(StringBuilder sb, double? value)
    {
        if (value.HasValue)
            sb.Append(value.Value.ToString("0.######", CultureInfo.InvariantCulture));
        else
            sb.Append(NullPartMarker);
        sb.Append('|');
    }

    private static void AppendNullableInt(StringBuilder sb, int? value)
    {
        if (value.HasValue)
            sb.Append(value.Value);
        else
            sb.Append(NullPartMarker);
        sb.Append('|');
    }

    /// <summary>
    /// Escape the two characters with structural meaning in the canonical
    /// string: <c>%</c> first (escape introducer), then the field
    /// separator <c>|</c>. Applied to every content string (names, values,
    /// resolved element names, descriptions) so user content can never
    /// shift field boundaries (ADR-049 known limitation, fixed in v3).
    /// </summary>
    internal static string Escape(string value)
    {
        if (value.Length == 0) return value;
        if (value.IndexOf('%') < 0 && value.IndexOf('|') < 0) return value;
        return value.Replace("%", "%25").Replace("|", "%7C");
    }

    /// <summary>
    /// A parameter value is "blank" (should not affect the hash) when it
    /// has no value, or has a value that carries no meaningful content.
    /// Marker strings are storage-type scoped (v3) so a user's literal
    /// "INVALID"/"UNSUPPORTED" text parameter still participates:
    /// <c>INVALID</c> is produced by the extractor only for ElementId
    /// storage, <c>UNSUPPORTED</c> only for unknown storage types.
    /// <c>READERROR</c> is blank for any storage type (accepted residual
    /// risk: a user literally typing "READERROR" in a text parameter —
    /// documented in ADR-056). Numeric zero IS meaningful and NOT blank.
    /// </summary>
    internal static bool IsBlankValue(bool hasValue, string? valueText, string storageType)
    {
        if (!hasValue) return true;
        if (valueText is null) return false;
        if (valueText.Length == 0) return true;
        if (valueText == "READERROR") return true;
        if (valueText == "INVALID")
            return string.Equals(storageType, "ElementId", StringComparison.Ordinal);
        if (valueText == "UNSUPPORTED")
            return storageType is not ("Double" or "Integer" or "String" or "ElementId");
        return false;
    }

    /// <summary>
    /// Parameter name is auto-generated by Revit and differs between
    /// documents (e.g. IFC GUID generated on every .rvt save).
    /// Such parameters must NOT participate in the content hash because
    /// they would make identical content produce different hashes across
    /// different documents / saves.
    /// </summary>
    private static bool IsAutoGeneratedParameter(string parameterName)
    {
        if (string.IsNullOrEmpty(parameterName)) return false;
        // IFC GUID — Revit regenerates this on every project save. Different
        // values for the same logical type between source project and mini-rvt.
        // Catch all locale variants: "Код IfcGUID", "IFC GUID", "IfcGUID", etc.
        return ContainsOrdinalIgnoreCase(parameterName, "IfcGUID")
            || ContainsOrdinalIgnoreCase(parameterName, "IFC GUID");
    }

    private static string FormatFlag(bool? value)
        => value.HasValue ? (value.Value ? "1" : "0") : AbsentMarker;

    private static string FormatSize(double? value)
        => value.HasValue
            ? value.Value.ToString("0.######", CultureInfo.InvariantCulture)
            : AbsentMarker;

    /// <summary>
    /// Coordinates (connector origins, bounding boxes) are rounded to
    /// 1e-4 ft (~0.03 mm) — fine enough to catch hand-moved connectors,
    /// coarse enough to absorb regen noise across Revit versions.
    /// </summary>
    private static string FormatCoord(double value)
        => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static bool ContainsOrdinalIgnoreCase(string haystack, string needle)
    {
#if NET8_0_OR_GREATER
        return haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
#else
        return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
#endif
    }

    private static string ComputeSha256Hex(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
#if NET8_0_OR_GREATER
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
#else
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(bytes);
        return BitConverter.ToString(hash).Replace("-", string.Empty);
#endif
    }
}
