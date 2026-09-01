using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Per-version store of segment routing rules (FHV21, owner decision
/// 2026-09-01): the segment configuration of a pipe type is mini-project
/// content — versioned like the segment size tables
/// (<see cref="ISegmentSizeRepository"/>), unlike fitting rules which are
/// item-level catalog links (ADR-072 World B). Readers
/// (routing tab / sync / drift probes) follow the catalog's
/// <c>current_version_label</c>, so a version rollback automatically
/// restores THAT version's segment configuration.
/// </summary>
public interface ISegmentRuleRepository
{
    /// <summary>Segment rules of one catalog version, in rule order per
    /// (family_key, type_name). Empty when the version has none (duct
    /// categories, pre-FHV21 rows awaiting backfill).</summary>
    Task<IReadOnlyList<SegmentRuleRecord>> ReadForVersionAsync(
        string catalogVersionId, CancellationToken ct = default);

    /// <summary>Segment rules of the item's CURRENT version (the pointer
    /// readers follow — a rollback automatically yields the activated
    /// version's configuration).</summary>
    Task<IReadOnlyList<SegmentRuleRecord>> ReadForCurrentVersionAsync(
        string catalogItemId, CancellationToken ct = default);

    /// <summary>Full replace of a version's segment rules (import /
    /// backfill write path — one transaction, delete + insert).</summary>
    Task ReplaceForVersionAsync(
        string catalogVersionId, IReadOnlyList<SegmentRuleRecord> rules, CancellationToken ct = default);

    /// <summary>Full replace against the item's CURRENT version (import
    /// write path — the just-imported version is current by then).</summary>
    Task ReplaceForCurrentVersionAsync(
        string catalogItemId, IReadOnlyList<SegmentRuleRecord> rules, CancellationToken ct = default);
}
