using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Storage of the catalog-version routing rules of system MEPCurve types
/// (ADR-072, V34 tables <c>family_routing_rules</c> +
/// <c>family_routing_type_settings</c>). Written at import from the live
/// project's routing (manager- or parameter-based); read by sync (the
/// mini-project carries no fittings, so routing syncs from DB data) and
/// by the routing editor (Phase 3).
/// </summary>
public interface IFamilyRoutingRuleRepository
{
    /// <summary>
    /// DELETE+INSERT the routing rules and per-type settings of one
    /// catalog version inside a single transaction (mirrors the
    /// dependency-repository pattern).
    /// </summary>
    Task ReplaceForVersionAsync(
        string catalogItemId,
        string catalogVersionId,
        IReadOnlyList<FamilyRoutingRuleInfo> rules,
        IReadOnlyList<FamilyRoutingTypeSettings> settings,
        CancellationToken ct = default);

    /// <summary>
    /// All routing rules and per-type settings of one catalog version
    /// (empty lists when the version carries no routing rows — the
    /// legacy-fallback discriminator, ADR-072 plan item 3).
    /// </summary>
    Task<(IReadOnlyList<FamilyRoutingRuleInfo> Rules, IReadOnlyList<FamilyRoutingTypeSettings> Settings)>
        ReadForVersionAsync(
            string catalogItemId,
            string catalogVersionId,
            CancellationToken ct = default);

    /// <summary>
    /// <c>true</c> when the version has at least one routing row (rule or
    /// type settings). <c>false</c> means "pre-V34 version — sync must
    /// fall back to reading routing from the mini-project" (legacy
    /// fallback; a legitimately routing-less type keeps its settings row,
    /// so presence is tracked by rows, not by rule count).
    /// </summary>
    Task<bool> HasRulesForVersionAsync(
        string catalogItemId,
        string catalogVersionId,
        CancellationToken ct = default);

    /// <summary>
    /// Same as <see cref="ReplaceForVersionAsync"/> but resolves the item's
    /// CURRENT version (join by <c>current_version_label</c>) — the import
    /// writes the routing of the version it just made active. No-op when
    /// the item has no current version.
    /// </summary>
    Task ReplaceForCurrentVersionAsync(
        string catalogItemId,
        IReadOnlyList<FamilyRoutingRuleInfo> rules,
        IReadOnlyList<FamilyRoutingTypeSettings> settings,
        CancellationToken ct = default);

    /// <summary>
    /// Same as <see cref="ReadForVersionAsync"/> but resolves the item's
    /// CURRENT version (join by <c>current_version_label</c>) — the sync
    /// path always reads the routing of the active version.
    /// </summary>
    Task<(IReadOnlyList<FamilyRoutingRuleInfo> Rules, IReadOnlyList<FamilyRoutingTypeSettings> Settings)>
        ReadForCurrentVersionAsync(
            string catalogItemId,
            CancellationToken ct = default);

    /// <summary>
    /// Same as <see cref="HasRulesForVersionAsync"/> for the item's
    /// CURRENT version.
    /// </summary>
    Task<bool> HasRulesForCurrentVersionAsync(
        string catalogItemId,
        CancellationToken ct = default);
}
