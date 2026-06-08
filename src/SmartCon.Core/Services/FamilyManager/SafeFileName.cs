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
}
