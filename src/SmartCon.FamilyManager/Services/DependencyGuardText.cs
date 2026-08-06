using SmartCon.Core.Models.FamilyManager;
using SmartCon.UI;

namespace SmartCon.FamilyManager.Services;

/// <summary>
/// Formats dependency-guard reference lists (E5, #213, ADR-067) for the
/// block dialog and the tree paperclip tooltip. Pure text logic over
/// <see cref="FamilyDependencyReference"/> — references of one parent
/// collapse into a single line «Parent» (v1, v2 (активная)).
/// </summary>
internal static class DependencyGuardText
{
    /// <summary>
    /// One line per referencing parent: «Name» (v1, v2 (активная)).
    /// Parents ordered by name, versions by label; the current version
    /// carries the localized "active" mark when
    /// <paramref name="includeCurrentMark"/> is set (block dialog — yes,
    /// paperclip tooltip — no, just «Name» (v2)).
    /// </summary>
    public static IReadOnlyList<string> FormatReferenceLines(
        IEnumerable<FamilyDependencyReference> references,
        bool includeCurrentMark = true)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(references);
#else
        if (references is null) throw new ArgumentNullException(nameof(references));
#endif

        var currentMark = includeCurrentMark
            ? LanguageManager.GetString(StringLocalization.Keys.FM_DependencyGuard_CurrentMark) ?? "active"
            : null;

        return references
            .GroupBy(r => r.ParentCatalogItemId)
            .OrderBy(g => g.First().ParentName, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var versions = g
                    .OrderBy(v => v.VersionLabel, StringComparer.OrdinalIgnoreCase)
                    .Select(v => v.IsCurrentVersion && currentMark is not null
                        ? $"{v.VersionLabel} ({currentMark})"
                        : v.VersionLabel);
                return $"«{g.First().ParentName}» ({string.Join(", ", versions)})";
            })
            .ToList();
    }
}
