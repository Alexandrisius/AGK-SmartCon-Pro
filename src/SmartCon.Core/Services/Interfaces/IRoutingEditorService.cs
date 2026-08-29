using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Routing editor engine (ADR-072, Phase 3): loads the routing rules of a
/// system MEPCurve catalog item for editing and saves edits AS A NEW
/// CATALOG VERSION — routing is versioned content (ROUTING sits in the
/// content hash, FHV19), so an edit clones the current version row with
/// recomputed content/per-type hashes and canonical sections
/// (<see cref="Implementation.FamilyContentHasher.RebuildSystemSectionsWithRouting"/>),
/// rewrites the routing tables and regenerates the
/// <c>family_dependencies</c> routing links (drift badges and the delete
/// guard keep working). No Revit involved — pure catalog data.
/// </summary>
public interface IRoutingEditorService
{
    /// <summary>
    /// Editor input: host category, types (with with/without-fittings
    /// discrimination), stored rules/settings and presence flags (part
    /// families missing from the catalog). <c>null</c> when the item is not
    /// a system MEPCurve item (the tab hides itself).
    /// </summary>
    Task<RoutingEditorData?> LoadAsync(string catalogItemId, CancellationToken ct = default);

    /// <summary>
    /// Saves the edited routing of the listed types as a new current
    /// version. Untouched types keep their stored rules verbatim. Refuses
    /// legacy versions without stored canonical sections (hashes would be
    /// unverifiable — the caller shows the actualization hint instead).
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
