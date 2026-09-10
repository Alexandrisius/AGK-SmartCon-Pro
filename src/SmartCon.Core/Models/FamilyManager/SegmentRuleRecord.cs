namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// One segment routing rule of a system pipe type — PER-VERSION storage
/// (FHV21, owner decision 2026-09-01, stress test баг 3): the mini-project
/// owns the whole segment configuration (which segments the type routes,
/// in which order, with which size-range criterion), so it is versioned
/// content like <see cref="SegmentSizeRecord"/> — unlike fitting rules,
/// which stay item-level catalog links (ADR-072 World B). Rollback to an
/// older version restores ITS segment configuration because readers follow
/// the <c>current_version_label</c> pointer.
/// </summary>
/// <param name="TypeName">System type name (routing is per-type).</param>
/// <param name="FamilyKey">Locale-invariant family key of the type.</param>
/// <param name="RuleOrder">Rule order inside the type's Segments group —
/// order decides priority when ranges of several segments overlap.</param>
/// <param name="SegmentName">Referenced segment (by name — segments are
/// project content resolved by name at sync).</param>
/// <param name="MinSizeFeet">Rule criterion Мин (feet);
/// <c>null</c> = unrestricted.</param>
/// <param name="MaxSizeFeet">Rule criterion Макс (feet);
/// <c>null</c> = unrestricted.</param>
/// <param name="Description">Rule comment (stored as-is).</param>
public sealed record SegmentRuleRecord(
    string TypeName,
    string FamilyKey,
    int RuleOrder,
    string SegmentName,
    double? MinSizeFeet,
    double? MaxSizeFeet,
    string Description);
