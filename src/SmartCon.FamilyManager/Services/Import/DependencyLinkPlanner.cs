using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.FamilyManager.Services.Import;

/// <summary>
/// ADR-066 (E1): pure planning of parent→child dependency links for the
/// Phase-3 batch executor. Separated from <see cref="ProjectFamilyBatchImportExecutor"/>
/// so the resolution rules are unit-testable without Revit/SQLite. A child
/// counts as linkable when it was IMPORTED in this batch (tracked by the
/// ORIGINAL dialog path — staging rewrites <c>FilePath</c> to the managed
/// .rfa path, so the staged item can never be matched back by path) OR when
/// it was skipped but already exists in the catalog (dedup-link). Parents
/// that were skipped/failed receive no links. E2 (#209): parents may be
/// loadable items too (shared-nested containers) — the caller merges system
/// and loadable parent ids into one map keyed by the ORIGINAL dialog path;
/// each link also records the embedded child version label (schema V30) for
/// dependency-drift detection.
/// </summary>
internal static class DependencyLinkPlanner
{
    public static DependencyLinkPlan Build(
        IReadOnlyList<FamilyBatchImportItem> items,
        IReadOnlyDictionary<string, string> importedParentItemIds,
        IReadOnlyCollection<string> importedLoadableOriginalPaths)
    {
        var linksByParent = new Dictionary<string, List<FamilyDependencyInfo>>(StringComparer.Ordinal);
        var unresolved = new List<FamilyBatchImportItem>();
        var parentNotImported = new List<(string ChildName, string ParentSourcePath)>();

        foreach (var child in items)
        {
            if (child.DependencyLinks is not { Count: > 0 }) continue;

            var childSkipped = child.Action == FamilyBatchImportAction.Skip;
            var childImported = importedLoadableOriginalPaths.Contains(child.FilePath);

            string? childCatalogItemId;
            string? childVersionLabel;
            if (childImported)
            {
                childCatalogItemId = child.PrecomputedCatalogItemId ?? child.ExistingCatalogItemId;
                // MakeActive imports no file — the embedded content is the
                // hash-matched version the user activated, not a fresh label.
                childVersionLabel = child.Action == FamilyBatchImportAction.MakeActive
                    ? child.MatchedVersionLabel
                    : child.PrecomputedVersionLabel;
            }
            else if ((childSkipped || child.Action == FamilyBatchImportAction.MakeActive)
                     && child.ExistingCatalogItemId is not null)
            {
                // Dedup-link (ADR-066): the dependency already exists in the
                // catalog — no re-import, but the parent still gets its link.
                // MakeActive lands here too: it imports no file (WasSkipped
                // in the executor results), so it never appears in the
                // imported-paths set, yet the link must record the activated
                // (hash-matched) version as the embedded one.
                // The embedded content is the hash-matched version; null when
                // the content matches no stored version (Existing+Skip) —
                // such links are drift-unknown (V30 NULL semantics).
                childCatalogItemId = child.ExistingCatalogItemId;
                childVersionLabel = child.MatchedVersionLabel;
            }
            else
            {
                unresolved.Add(child);
                continue;
            }

            if (string.IsNullOrEmpty(childCatalogItemId))
            {
                unresolved.Add(child);
                continue;
            }

            foreach (var link in child.DependencyLinks)
            {
                if (!importedParentItemIds.TryGetValue(link.ParentSourcePath, out var parentId))
                {
                    parentNotImported.Add((child.FileName, link.ParentSourcePath));
                    continue;
                }

                if (!linksByParent.TryGetValue(parentId, out var list))
                {
                    list = new List<FamilyDependencyInfo>();
                    linksByParent.Add(parentId, list);
                }

                list.Add(new FamilyDependencyInfo(childCatalogItemId!, link.Kind, link.PartName, list.Count, childVersionLabel));
            }
        }

        return new DependencyLinkPlan(linksByParent, unresolved, parentNotImported);
    }
}

/// <summary>
/// Result of <see cref="DependencyLinkPlanner.Build"/>: link lists grouped by
/// parent catalog item id, plus the dependency rows whose links could not be
/// resolved (skipped-new without a catalog item, or failed imports) and the
/// links whose parent was skipped/failed in this batch — the caller logs both.
/// </summary>
internal sealed record DependencyLinkPlan(
    IReadOnlyDictionary<string, List<FamilyDependencyInfo>> LinksByParent,
    IReadOnlyList<FamilyBatchImportItem> UnresolvedChildren,
    IReadOnlyList<(string ChildName, string ParentSourcePath)> LinksWithParentNotImported);
