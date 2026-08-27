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
/// Issue #249 (Phase 1): the monolithic canonical builders are decomposed
/// into per-section builders (<see cref="ContentSectionHash"/>). The full
/// canonical string is the concatenation of all sections in canonical
/// order — byte-identical to the pre-refactor format, so there is NO FHV
/// bump and NO database migration. Sections are an analytics layer
/// (per-section diff, change classification, per-type hashes); the single
/// identity hash is unchanged.
/// </remarks>
public sealed partial class FamilyContentHasher : IFamilyContentHasher
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

    public string? BuildLoadableCanonicalStringForDiagnostics(FamilySnapshot snapshot)
    {
        return snapshot is null ? null : BuildLoadableCanonicalString(snapshot);
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

    /// <inheritdoc/>
    public IReadOnlyList<ContentSectionHash>? ComputeSectionsForLoadable(FamilySnapshot snapshot)
    {
        return snapshot is null ? null : BuildLoadableSections(snapshot);
    }

    /// <inheritdoc/>
    public IReadOnlyList<ContentSectionHash>? ComputeSectionsForSystem(SystemFamilySnapshot snapshot)
    {
        if (snapshot is null || snapshot.Types.Count == 0)
            return null;
        return BuildSystemSections(snapshot);
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string>? ComputePerTypeHashesForLoadable(FamilySnapshot snapshot)
    {
        if (snapshot is null)
            return null;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in snapshot.Types)
        {
            map[t.Name] = ComputeSha256Hex(BuildLoadableTypeSubstring(t));
        }
        return map;
    }

    /// <inheritdoc/>
    public IReadOnlyList<SystemTypeContentHash>? ComputePerTypeHashesForSystem(SystemFamilySnapshot snapshot)
    {
        if (snapshot is null || snapshot.Types.Count == 0)
            return null;

        var list = new List<SystemTypeContentHash>(snapshot.Types.Count);
        foreach (var t in snapshot.Types)
        {
            list.Add(new SystemTypeContentHash(
                t.Name,
                t.FamilyKey,
                t.FamilyName,
                ComputeSha256Hex(BuildSystemTypeBody(t))));
        }
        return list;
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
    /// Appends one parameter value entry to the canonical string (shared by
    /// the TYPES and FHV9 PHANTOM sections). Blank and auto-generated
    /// values are skipped.
    /// </summary>
    private static void AppendParameterValue(StringBuilder sb, FamilyParameterValue v)
    {
        if (IsBlankValue(v.HasValue, v.ValueText, v.StorageType)) return;
        if (IsAutoGeneratedParameter(v.ParameterName)) return;
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
