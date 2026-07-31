using System.Text.RegularExpressions;

namespace SmartCon.Core.Services.FamilyManager;

/// <summary>
/// Issue #188: pure path-based fallback check for SmartCon reference
/// mini-projects staged in managed storage
/// (<c>{dbRoot}\files\{catalogItemId}\{versionLabel}\{name}.rvt</c>).
/// Used where the ES marker (<see cref="Interfaces.IMiniProjectMarker"/>) may
/// be absent — legacy files staged before the marker existed.
/// </summary>
/// <remarks>
/// Conservative by design: a false positive would hide a real work project
/// from active-DB switching, so the pattern requires the full managed-storage
/// shape (<c>\files\{guid}\v{label}\*.rvt</c>). Closing decisions (#186) do
/// NOT use this fallback — only the ES marker authorizes a close.
/// </remarks>
public static class MiniProjectPathPattern
{
    private static readonly Regex MiniProjectPathRegex = new(
        @"\\files\\[0-9a-fA-F-]{32,36}\\v[^\\]+\\[^\\]+\.rvt$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// True when <paramref name="path"/> has the managed-storage mini-project
    /// shape. Empty/null → false.
    /// </summary>
    public static bool IsMiniProjectPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        return MiniProjectPathRegex.IsMatch(path);
    }
}
