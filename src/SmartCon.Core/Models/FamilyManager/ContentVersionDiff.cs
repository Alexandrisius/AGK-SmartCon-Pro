namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// The "what changed" report between an incoming family and the active
/// catalog version (Issue #249, Phase 4): the change classification, the
/// differing section keys (flattened — per-type keys for system families
/// carry <c>"SECTION|{TypeName}"</c>) and the per-type resolution
/// (changed / added / removed type names, display-ready).
/// </summary>
public sealed record ContentVersionDiff(
    ContentChangeClass Class,
    IReadOnlyList<string> ChangedSections,
    IReadOnlyList<string> ChangedTypes,
    IReadOnlyList<string> AddedTypes,
    IReadOnlyList<string> RemovedTypes);
