using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Implementation;

/// <summary>
/// Computes the "what changed" report between an incoming family (its
/// Prepare-time sections and per-type hashes, in memory) and the active
/// catalog version (its stored section hashes and per-type hashes from
/// <c>catalog_versions</c> / <c>family_type_hashes</c>) — Issue #249,
/// Phase 4. Pure C#: no Revit, no database.
/// </summary>
public static class ContentVersionDiffComputer
{
    /// <summary>
    /// Section keys are compared by SHA-256 hex: a section is "changed"
    /// when its hash differs or it exists on only one side. Per-type
    /// comparison is keyed by the type identity key (case-insensitive):
    /// changed = present on both sides with a different hash; added =
    /// incoming only; removed = active only. All output lists are sorted
    /// Ordinal for a stable UI.
    /// </summary>
    public static ContentVersionDiff Compute(
        IReadOnlyList<ContentSectionHash> incomingSections,
        IReadOnlyList<FamilyTypeHashEntry>? incomingTypeHashes,
        IReadOnlyDictionary<string, string>? activeSectionHashes,
        IReadOnlyList<FamilyTypeHashEntry>? activeTypeHashes)
    {
        var changedSections = new List<string>();
        var activeHashes = activeSectionHashes ?? new Dictionary<string, string>();
        var incomingKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in incomingSections)
        {
            var key = section.Key;
            incomingKeys.Add(key);
            if (!activeHashes.TryGetValue(key, out var activeHash)
                || !string.Equals(activeHash, section.HashHex, StringComparison.Ordinal))
            {
                changedSections.Add(key);
            }
        }
        foreach (var key in activeHashes.Keys)
        {
            if (!incomingKeys.Contains(key))
            {
                changedSections.Add(key);
            }
        }
        changedSections.Sort(StringComparer.Ordinal);

        var changedTypes = new List<string>();
        var addedTypes = new List<string>();
        var removedTypes = new List<string>();
        var activeByKey = (activeTypeHashes ?? (IReadOnlyList<FamilyTypeHashEntry>)[])
            .ToDictionary(t => t.TypeIdentityKey, t => t, StringComparer.OrdinalIgnoreCase);
        var incomingKeys2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var incoming in incomingTypeHashes ?? (IReadOnlyList<FamilyTypeHashEntry>)[])
        {
            incomingKeys2.Add(incoming.TypeIdentityKey);
            if (!activeByKey.TryGetValue(incoming.TypeIdentityKey, out var active))
            {
                addedTypes.Add(incoming.TypeName);
            }
            else if (!string.Equals(active.HashHex, incoming.HashHex, StringComparison.Ordinal))
            {
                changedTypes.Add(incoming.TypeName);
            }
        }
        foreach (var active in activeByKey.Values)
        {
            if (!incomingKeys2.Contains(active.TypeIdentityKey))
            {
                removedTypes.Add(active.TypeName);
            }
        }
        changedTypes.Sort(StringComparer.Ordinal);
        addedTypes.Sort(StringComparer.Ordinal);
        removedTypes.Sort(StringComparer.Ordinal);

        return new ContentVersionDiff(
            ContentChangeClassifier.Classify(changedSections),
            changedSections,
            changedTypes,
            addedTypes,
            removedTypes);
    }
}
