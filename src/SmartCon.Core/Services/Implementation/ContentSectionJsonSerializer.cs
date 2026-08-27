using System.Text.Json;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Implementation;

/// <summary>
/// JSON persistence of the canonical content sections of a catalog
/// version (Issue #249, Phase 4): two flat name→value maps stored in the
/// <c>catalog_versions.section_hashes</c> and
/// <c>catalog_versions.section_strings</c> columns. System families
/// carry per-type entries (<see cref="ContentSectionHash.TypeName"/>) —
/// their keys are flattened via <see cref="ContentSectionHash.Key"/> so
/// the map stays flat and collision-free. Entries are written in the
/// input list's order (the canonical section order the hasher emits).
/// </summary>
public static class ContentSectionJsonSerializer
{
    /// <summary>Section name → section SHA-256 hex (canonical order).</summary>
    public static string SerializeHashes(IReadOnlyList<ContentSectionHash> sections)
    {
        var map = BuildMap(sections, s => s.HashHex);
        return JsonSerializer.Serialize(map);
    }

    /// <summary>Section name → canonical substring (canonical order).</summary>
    public static string SerializeStrings(IReadOnlyList<ContentSectionHash> sections)
    {
        var map = BuildMap(sections, s => s.CanonicalString);
        return JsonSerializer.Serialize(map);
    }

    /// <summary>
    /// Parse a section map written by <see cref="SerializeHashes"/> /
    /// <see cref="SerializeStrings"/>. Returns <c>null</c> for null,
    /// empty or malformed payloads (a corrupt analytics column is never
    /// evidence — callers fall back to recomputation).
    /// </summary>
    public static IReadOnlyDictionary<string, string>? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json!);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> BuildMap(
        IReadOnlyList<ContentSectionHash> sections,
        Func<ContentSectionHash, string> value)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var section in sections)
        {
            map[section.Key] = value(section);
        }
        return map;
    }
}
