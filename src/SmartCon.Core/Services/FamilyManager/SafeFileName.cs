using System.Text;

namespace SmartCon.Core.Services.FamilyManager;

/// <summary>
/// Safe file name utilities that preserve dots inside family names.
/// Unlike <see cref="System.IO.Path.GetFileNameWithoutExtension(string?)"/>,
/// which truncates by the FIRST dot when the input has no recognized extension
/// (e.g. "BP_A0307_ITAP_ART.162_Амер угловая" → "BP_A0307_ITAP_ART"),
/// this helper only strips KNOWN extensions (.rfa, .rvt, .txt) and
/// preserves all other dots — required for family names that use
/// dots as type separators (FamilyName.TypeName).
/// </summary>
public static class SafeFileName
{
    private static readonly string[] KnownExtensions =
        { ".rfa", ".rvt", ".txt" };

    /// <summary>
    /// Returns the file name without extension, but only strips known
    /// Revit / catalog extensions. If no known extension is present,
    /// returns the file name as-is (preserving internal dots).
    /// </summary>
    /// <example>
    /// <code>
    /// GetBaseName("BP_A0307_ITAP_ART.162_Амер угловая.rfa")
    ///   → "BP_A0307_ITAP_ART.162_Амер угловая"
    /// GetBaseName("BP_A0307_ITAP_ART.162_Амер угловая")
    ///   → "BP_A0307_ITAP_ART.162_Амер угловая"  // NO truncation!
    /// GetBaseName("MyFamily.rfa")
    ///   → "MyFamily"
    /// GetBaseName("MyFamily.rvt")
    ///   → "MyFamily"
    /// </code>
    /// </example>
    public static string GetBaseName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var name = Path.GetFileName(path);
        foreach (var ext in KnownExtensions)
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^ext.Length];
            }
        }
        return name;
    }

    /// <summary>
    /// Returns a file name safe to use on the current platform by replacing
    /// every <see cref="Path.GetInvalidFileNameChars"/> character with
    /// <c>'_'</c>. Empty / whitespace input is normalised to <c>"Family"</c>
    /// so the resulting on-disk name is never blank.
    /// </summary>
    /// <remarks>
    /// v2.0.0: single source of truth for the on-disk file-name sanitisation
    /// rule. Both the local-catalog path resolver and the staging helpers
    /// (in <c>SmartCon.FamilyManager</c> and <c>SmartCon.Revit</c>) go
    /// through this helper so the relative path stored in
    /// <c>family_files.relative_path</c> always agrees with the file
    /// actually written under <c>{dbRoot}/files/&lt;id&gt;/&lt;version&gt;/</c>.
    /// Living in <c>SmartCon.Core</c> makes it usable from
    /// <c>SmartCon.Revit</c> (no project reference cycle) and from
    /// unit-test fixtures alike.
    /// </remarks>
    public static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Family";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name!.Length);
        foreach (var c in name)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }
        return sb.ToString();
    }
}
