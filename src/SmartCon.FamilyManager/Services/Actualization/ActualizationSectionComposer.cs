using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.Actualization;

/// <summary>
/// Shared loadable hash+section computation for the actualization tasks:
/// composed with the shared-nested closure when the extraction carried it
/// (the same enriched snapshot as the identity hash — composite-consistent
/// NESTEDHASH). One <see cref="CompositeFamilyHashComposer.ComposeDetailed"/>
/// pass yields both the identity hash and the section analytics. Used by
/// <see cref="HashFormatActualizationTask"/> (writes sections inline with
/// the FHV12 recompute so the migration completes in one pass) and
/// <see cref="SectionHashesActualizationTask"/> (backstop for versions
/// whose section columns are still NULL).
/// </summary>
internal static class ActualizationSectionComposer
{
    public static (FamilyContentHash? Hash, IReadOnlyList<ContentSectionHash>? Sections) ComputeLoadable(
        FamilyActualizationContext context,
        IFamilyContentHasher contentHasher,
        CompositeFamilyHashComposer compositeComposer)
    {
        if (context.SharedNestedSubtrees is not { Count: > 0 } subtrees)
        {
            return (
                contentHasher.ComputeForLoadable(context.Snapshot),
                contentHasher.ComputeSectionsForLoadable(context.Snapshot));
        }

        var snapshots = new Dictionary<string, FamilySnapshot>(StringComparer.OrdinalIgnoreCase);
        var rootName = FamilyNameNormalizer.Normalize(context.Snapshot.FamilyName);
        snapshots[rootName] = context.Snapshot;
        foreach (var nested in context.SharedNestedSnapshots ?? (IReadOnlyList<FamilySnapshot>)Array.Empty<FamilySnapshot>())
        {
            // Last-wins on a normalized-name collision (same heuristic as
            // the import-time preparation queue).
            snapshots[FamilyNameNormalizer.Normalize(nested.FamilyName)] = nested;
        }

        var flatSubtrees = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var subtree in subtrees)
        {
            flatSubtrees[subtree.OwnerFamilyName] = subtree.NestedFamilyNames;
        }

        var composed = compositeComposer.ComposeDetailed(snapshots, flatSubtrees);
        return composed.TryGetValue(rootName, out var result)
            ? (result.Hash, result.Sections)
            : (contentHasher.ComputeForLoadable(context.Snapshot),
               contentHasher.ComputeSectionsForLoadable(context.Snapshot));
    }
}
