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
    private const string EmptyValueMarker = "EMPTY";
    private const string NullElementMarker = "NULLELEMENT";
    private const string NullFormulaMarker = "NOFORMULA";
    private const string NullGuidMarker = "NOGUID";
    private const string NullBuiltInMarker = "NOBUILTIN";
    private const string NullSubcatMarker = "NOSUBCAT";

    public FamilyContentHash? ComputeForLoadable(FamilySnapshot snapshot)
    {
        if (snapshot is null)
            return null;

        if (snapshot.Parameters.Count == 0 &&
            snapshot.Types.Count == 0 &&
            snapshot.Geometry.TotalFormCount == 0)
        {
            using var _scope = SmartConLogger.BeginScope("ContentHash",
                ("Method", nameof(ComputeForLoadable)),
                ("Family", snapshot.FamilyName),
                ("Result", "EmptySnapshot"));
            SmartConLogger.Warn("Loadable snapshot has no parameters, types, or geometry — hash not computed. [Action: check family document is valid]");
            return null;
        }

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
        sb.Append(snapshot.FamilyName ?? string.Empty);
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
                sb.Append(v.ParameterName).Append('|');
                sb.Append(v.StorageType).Append('|');
                sb.Append(v.HasValue ? 'V' : 'E').Append('|');
                if (!v.HasValue)
                {
                    sb.Append(EmptyValueMarker).Append('|');
                }
                else
                {
                    if (v.ValueNumber.HasValue)
                        sb.Append(v.ValueNumber.Value.ToString("0.######", CultureInfo.InvariantCulture));
                    else
                        sb.Append(v.ValueText ?? string.Empty);
                    sb.Append('|');
                    sb.Append(v.ResolvedElementName ?? NullElementMarker).Append('|');
                }
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
                sb.Append(v.ParameterName).Append('|');
                sb.Append(v.StorageType).Append('|');
                sb.Append(v.HasValue ? 'V' : 'E').Append('|');
                if (!v.HasValue)
                {
                    sb.Append(EmptyValueMarker).Append('|');
                }
                else
                {
                    if (v.ValueNumber.HasValue)
                        sb.Append(v.ValueNumber.Value.ToString("0.######", CultureInfo.InvariantCulture));
                    else
                        sb.Append(v.ValueText ?? string.Empty);
                    sb.Append('|');
                    sb.Append(v.ResolvedElementName ?? NullElementMarker).Append('|');
                }
            }
        }

        return sb.ToString();
    }

    private static string ComputeSha256Hex(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }
}
