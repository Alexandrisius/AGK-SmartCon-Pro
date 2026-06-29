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
/// stable across SaveAs, rename, and Revit upgrade.
/// </summary>
public sealed class FamilyContentHasher : IFamilyContentHasher
{
    private const string NullElementMarker = "NULLELEMENT";
    private const string NullFormulaMarker = "NOFORMULA";
    private const string NullGuidMarker = "NOGUID";
    private const string NullBuiltInMarker = "NOBUILTIN";
    private const string NullSubcatMarker = "NOSUBCAT";

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
            ("Hash", hex));
        SmartConLogger.Info($"Loadable hash computed: {hex} ({snapshot.Parameters.Count} params, {snapshot.Types.Count} types, {snapshot.Geometry.TotalFormCount} forms)");
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
        SmartConLogger.Info($"System hash computed: {hex} ({snapshot.CategoryName}, {snapshot.Types.Count} types)");
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
    /// Format: FHV1|LOADABLE|{name}|{cat}|PARAMS|...|TYPES|...|GEOM|...|NESTED|...
    /// </summary>
    internal static string BuildLoadableCanonicalString(FamilySnapshot snapshot)
    {
        var sb = new StringBuilder(512);
        sb.Append("FHV1|LOADABLE|");
        sb.Append(StripRfaExtension(snapshot.FamilyName) ?? string.Empty);
        sb.Append('|');
        sb.Append(snapshot.Category ?? string.Empty);
        sb.Append('|');

        sb.Append("PARAMS|");
        var sortedParams = snapshot.Parameters
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.StorageType, StringComparer.Ordinal);
        foreach (var p in sortedParams)
        {
            sb.Append(p.Name).Append('|');
            sb.Append(p.StorageType).Append('|');
            sb.Append(p.ParameterGroup ?? string.Empty).Append('|');
            sb.Append(p.IsInstance ? 'I' : 'T').Append('|');
            sb.Append(p.IsShared ? 'S' : 'P').Append('|');
            sb.Append(p.Formula ?? NullFormulaMarker).Append('|');
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
            sb.Append(t.Name).Append('|');
            var sortedValues = t.Values
                .OrderBy(v => v.ParameterName, StringComparer.Ordinal);
            foreach (var v in sortedValues)
            {
                if (IsBlankValue(v.HasValue, v.ValueText)) continue;
                if (IsAutoGeneratedParameter(v.ParameterName)) continue;
                sb.Append(v.ParameterName).Append('|');
                sb.Append(v.StorageType).Append('|');
                sb.Append('V').Append('|');
                if (v.ValueNumber.HasValue)
                    sb.Append(v.ValueNumber.Value.ToString("0.######", CultureInfo.InvariantCulture));
                else
                    sb.Append(v.ValueText ?? string.Empty);
                sb.Append('|');
                sb.Append(v.ResolvedElementName ?? NullElementMarker).Append('|');
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
            sb.Append(f.SubcategoryName ?? NullSubcatMarker).Append('|');
        }

        sb.Append("GEOM2D|");
        sb.Append(snapshot.Geometry.SymbolicCurveCount).Append('|');
        sb.Append(snapshot.Geometry.DetailCurveCount).Append('|');
        sb.Append(snapshot.Geometry.ModelCurveCount).Append('|');
        sb.Append(snapshot.Geometry.TextNoteCount).Append('|');
        sb.Append(snapshot.Geometry.ReferencePlaneCount).Append('|');
        sb.Append(snapshot.Geometry.DimensionCount).Append('|');

        sb.Append("NESTED|");
        var sortedNested = snapshot.SharedNestedFamilyNames
            .OrderBy(n => n, StringComparer.Ordinal);
        foreach (var n in sortedNested)
        {
            sb.Append(n).Append('|');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Build the canonical string for a system family snapshot.
    /// Format: FHV1|SYSTEM|{catName}|{catId}|TYPES|...
    /// </summary>
    internal static string BuildSystemCanonicalString(SystemFamilySnapshot snapshot)
    {
        var sb = new StringBuilder(256);
        sb.Append("FHV1|SYSTEM|");
        sb.Append(snapshot.CategoryName ?? string.Empty);
        sb.Append('|');
        sb.Append(snapshot.CategoryId).Append('|');

        sb.Append("TYPES|");
        var sortedTypes = snapshot.Types
            .OrderBy(t => t.Name, StringComparer.Ordinal);
        foreach (var t in sortedTypes)
        {
            sb.Append(t.Name).Append('|');
            var sortedValues = t.Values
                .OrderBy(v => v.ParameterName, StringComparer.Ordinal);
            foreach (var v in sortedValues)
            {
                if (IsBlankValue(v.HasValue, v.ValueText)) continue;
                if (IsAutoGeneratedParameter(v.ParameterName)) continue;
                sb.Append(v.ParameterName).Append('|');
                sb.Append(v.StorageType).Append('|');
                sb.Append('V').Append('|');
                if (v.ValueNumber.HasValue)
                    sb.Append(v.ValueNumber.Value.ToString("0.######", CultureInfo.InvariantCulture));
                else
                    sb.Append(v.ValueText ?? string.Empty);
                sb.Append('|');
                sb.Append(v.ResolvedElementName ?? NullElementMarker).Append('|');
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// A parameter value is "blank" (should not affect the hash) when it
    /// has no value, or has a value that carries no meaningful content:
    /// empty string, INVALID (ElementId with no element), UNSUPPORTED
    /// (unknown storage type), READERROR (failed to read).
    /// Numeric zero IS meaningful (e.g. IFC=0) and is NOT blank.
    /// </summary>
    private static bool IsBlankValue(bool hasValue, string? valueText)
    {
        if (!hasValue) return true;
        if (valueText is null) return false;
        return valueText.Length == 0
            || valueText == "INVALID"
            || valueText == "UNSUPPORTED"
            || valueText == "READERROR";
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

    /// <summary>
    /// Strips a trailing <c>.rfa</c> extension (case-insensitive) from the
    /// family name. <c>Document.Title</c> may or may not include the
    /// extension depending on how the family document was opened
    /// (<c>EditFamily</c> from a project includes it; opening a managed
    /// <c>.rfa</c> file directly does not). Stripping here is a defensive
    /// measure so the hash is stable regardless of the source path.
    /// </summary>
    private static string StripRfaExtension(string? name)
    {
        if (name is null || name.Length == 0) return string.Empty;
        if (name.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
            return name.Substring(0, name.Length - 4);
        return name;
    }
}
