using System.Text;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Implementation;

/// <summary>
/// Routing-editor support (ADR-072, Phase 3): recompute a system version's
/// content hash, per-type hashes and canonical sections after routing rules
/// were edited IN THE DATABASE (no Revit, no snapshot). The version's stored
/// <c>section_strings</c> (V33) carry every other section verbatim — only
/// the ROUTING sections of the edited types are re-formatted from the new
/// rules, everything else is concatenated back in the exact canonical order
/// of <c>BuildSystemSections</c>, so the recomputed hashes stay byte-exact
/// with what <see cref="ComputeForSystem"/> /
/// <see cref="ComputePerTypeHashesForSystem"/> would produce from a live
/// re-extraction (covered by unit tests against the real hasher path).
/// </summary>
public sealed partial class FamilyContentHasher
{
    // Canonical per-type section order of BuildSystemSections — changing it
    // here without changing the hasher breaks byte-exactness by definition.
    private static readonly string[] SystemTypeSectionOrder =
    [
        FamilyContentSectionNames.Values,
        FamilyContentSectionNames.FamKey,
        FamilyContentSectionNames.Struct,
        FamilyContentSectionNames.Routing,
        FamilyContentSectionNames.Segments,
        FamilyContentSectionNames.Subtypes,
        FamilyContentSectionNames.Railing,
        FamilyContentSectionNames.Wire,
    ];

    /// <summary>
    /// The canonical ROUTING section string of one routing snapshot
    /// (<c>ROUTING|-|</c> for <c>null</c>) — byte-identical to the section
    /// the extractor/hash path emits for the same content.
    /// </summary>
    public static string FormatSystemRoutingSection(RoutingPreferencesSnapshot? routing)
        => BuildRoutingSubstring(routing);

    /// <summary>
    /// Rebuilds the full ordered section list of a system version from its
    /// stored section strings, substituting the ROUTING sections of the
    /// types present in <paramref name="newRoutingByType"/> (key = type
    /// name). Returns the recomputed content hash and the per-type hash
    /// entries of EVERY type (unchanged types recompute to their previous
    /// values — the caller replaces the whole set).
    /// </summary>
    /// <param name="currentStrings">
    /// The version's stored <c>section_strings</c> map
    /// (<see cref="ContentSectionJsonSerializer.Deserialize"/>). Must
    /// contain the META section and all 8 sections of every type — a
    /// missing entry means corrupt/legacy storage and throws
    /// (<see cref="InvalidOperationException"/>); the caller refuses the
    /// edit instead of writing unverifiable hashes.
    /// </param>
    /// <param name="types">
    /// Identity of every type of the version (name + family key + family
    /// name) — the canonical sort order needs all three
    /// (<c>SortSystemTypes</c>).
    /// </param>
    /// <param name="newRoutingByType">
    /// Edited routing per type name; a type absent from the dictionary
    /// keeps its stored ROUTING section. An explicit <c>null</c> value
    /// means "routing removed" (canonical empty section).
    /// </param>
    public static RecomposedSystemSections RebuildSystemSectionsWithRouting(
        IReadOnlyDictionary<string, string> currentStrings,
        IReadOnlyList<RecomposeTypeIdentity> types,
        IReadOnlyDictionary<string, RoutingPreferencesSnapshot?> newRoutingByType)
    {
        if (!currentStrings.TryGetValue(FamilyContentSectionNames.Meta, out var meta))
        {
            throw new InvalidOperationException(
                "Stored section_strings have no META section — the version is legacy/corrupt; reimport or run the section-hashes actualization");
        }

        var orderedTypes = types
            .OrderBy(t => t.TypeName, StringComparer.Ordinal)
            .ThenBy(t => t.FamilyKey ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(t => t.FamilyName ?? string.Empty, StringComparer.Ordinal)
            .ToList();

        var sections = new List<ContentSectionHash>(1 + orderedTypes.Count * SystemTypeSectionOrder.Length)
        {
            new(FamilyContentSectionNames.Meta, meta, ComputeSha256Hex(meta)),
        };

        var typeHashes = new List<FamilyTypeHashEntry>(orderedTypes.Count);
        var fullCanonical = new StringBuilder(meta.Length + orderedTypes.Count * 512);
        fullCanonical.Append(meta);

        foreach (var type in orderedTypes)
        {
            var body = new StringBuilder(512);
            foreach (var sectionName in SystemTypeSectionOrder)
            {
                string canonical;
                if (sectionName == FamilyContentSectionNames.Routing
                    && newRoutingByType.TryGetValue(type.TypeName, out var newRouting))
                {
                    canonical = BuildRoutingSubstring(newRouting);
                }
                else if (!currentStrings.TryGetValue(sectionName + "|" + type.TypeName, out canonical!))
                {
                    throw new InvalidOperationException(
                        $"Stored section_strings miss '{sectionName}|{type.TypeName}' — the version is legacy/corrupt; reimport or run the section-hashes actualization");
                }

                sections.Add(new ContentSectionHash(
                    sectionName, canonical, ComputeSha256Hex(canonical), type.TypeName));
                body.Append(canonical);
            }

            fullCanonical.Append(body);
            typeHashes.Add(FamilyTypeHashEntry.ForSystemType(new SystemTypeContentHash(
                type.TypeName, type.FamilyKey, type.FamilyName, ComputeSha256Hex(body.ToString()))));
        }

        return new RecomposedSystemSections(
            sections,
            ComputeSha256Hex(fullCanonical.ToString()),
            typeHashes);
    }
}

/// <summary>Type identity for section recomposition (the canonical
/// per-type sort needs name + family key + family name).</summary>
public sealed record RecomposeTypeIdentity(string TypeName, string? FamilyKey, string? FamilyName);

/// <summary>Result of <see cref="FamilyContentHasher.RebuildSystemSectionsWithRouting"/>.</summary>
public sealed record RecomposedSystemSections(
    IReadOnlyList<ContentSectionHash> Sections,
    string ContentHashHex,
    IReadOnlyList<FamilyTypeHashEntry> TypeHashes);
