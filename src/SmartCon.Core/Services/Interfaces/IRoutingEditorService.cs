using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Routing editor engine (ADR-072, World B — owner decision 2026-08-29):
/// routing is a link between catalog families, NOT file content — it lives
/// in the item-level V37 tables, outside the content hash and the version
/// model. Save edits the links IN PLACE (no version is created, no hash is
/// recomputed) and regenerates the current version's
/// <c>family_dependencies</c> routing links (drift badges and the delete
/// guard keep working). No Revit involved — pure catalog data.
/// </summary>
public interface IRoutingEditorService
{
    /// <summary>
    /// Editor input: host category, types (with with/without-fittings
    /// discrimination), stored item-level rules/settings (V34 current-
    /// version fallback for legacy items) and presence flags (part
    /// families missing from the catalog). <c>null</c> when the item is not
    /// a system MEPCurve item (the tab hides itself).
    /// </summary>
    Task<RoutingEditorData?> LoadAsync(string catalogItemId, CancellationToken ct = default);

    /// <summary>
    /// Saves the edited routing of the listed types in place (item-level
    /// tables + current-version dependency links). Untouched types keep
    /// their stored rules verbatim. Never creates a catalog version.
    /// </summary>
    Task<RoutingSaveResult> SaveAsync(
        string catalogItemId, RoutingEditorSave save, CancellationToken ct = default);

    /// <summary>
    /// Part picker source: active loadable catalog items of the given
    /// fitting Revit category whose <c>part_type</c> fact matches one of
    /// the ordinals (empty ordinal set = no part-type filter), with the
    /// type names of their current version. Strictly as Revit filters the
    /// routing dialog (ADR-072 §2.7 item 4) — param.Set/AddRule reject
    /// incompatible parts, so the filter is mandatory.
    /// </summary>
    Task<IReadOnlyList<RoutingPartCandidate>> GetPartCandidatesAsync(
        int fittingCategoryId,
        IReadOnlyCollection<int> partTypeOrdinals,
        CancellationToken ct = default);
}
