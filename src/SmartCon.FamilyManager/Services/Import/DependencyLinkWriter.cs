using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.Import;

/// <summary>
/// ADR-066: persists parent→child dependency links to
/// <c>family_dependencies</c> after the main import loop. Shared by both
/// batch executors (file-based UC-1 and project-based UC-3/4) since E2
/// (#209): shared-nested dependencies appear in both flows. The resolution
/// rules live in <see cref="DependencyLinkPlanner"/> (pure, unit-tested);
/// this writer only logs unresolved children and writes the planned links
/// to each parent's CURRENT version
/// (<see cref="IFamilyDependencyRepository.ReplaceForCurrentVersionAsync"/>).
/// Failures never roll back the import — the sync falls back to name-based
/// resolution and the links can be recreated by re-importing the parent
/// (ADR-066 §5.3).
/// </summary>
internal static class DependencyLinkWriter
{
    public static async Task WriteAsync(
        IReadOnlyList<FamilyBatchImportItem> items,
        IReadOnlyDictionary<string, string> importedParentItemIds,
        IReadOnlyCollection<string> importedLoadableOriginalPaths,
        IFamilyDependencyRepository repository,
        IFamilyRoutingRuleRepository? routingRuleRepository,
        IFamilyCatalogProvider? catalog,
        CancellationToken ct)
    {
        using var _scope = SmartConLogger.BeginScope("BatchImport",
            ("Method", nameof(WriteAsync)));

        var linksByParent = new Dictionary<string, List<FamilyDependencyInfo>>(StringComparer.Ordinal);
        var children = items.Where(i => i.DependencyLinks is { Count: > 0 }).ToList();
        if (children.Count > 0)
        {
            var plan = DependencyLinkPlanner.Build(items, importedParentItemIds, importedLoadableOriginalPaths);

            foreach (var child in plan.UnresolvedChildren)
            {
                var reason = child.Action == FamilyBatchImportAction.Skip
                    ? "skipped and is not in the catalog"
                    : "not imported (error)";
                SmartConLogger.Warn(
                    $"Dependency links for '{child.FileName}' not written: the dependency row was {reason}. " +
                    "[Action: проверьте строку зависимости в batch-диалоге и повторите импорт родителя]");
            }

            foreach (var (childName, parentSourcePath) in plan.LinksWithParentNotImported)
            {
                SmartConLogger.Debug(
                    $"Link '{childName}' → '{parentSourcePath}' not written: parent was skipped or failed");
            }

            linksByParent = new Dictionary<string, List<FamilyDependencyInfo>>(StringComparer.Ordinal);
            foreach (var pair in plan.LinksByParent)
            {
                linksByParent[pair.Key] = new List<FamilyDependencyInfo>(pair.Value);
            }
        }

        // ADR-072 (plan item 5): reimport from a slim mini-project collects
        // NO routing dependencies (the mini carries no fitting rules) —
        // without this the new version would lose its family_dependencies
        // links (guard/paperclip/drift degrade to name-fallback). Links are
        // regenerated from the routing rules just stored by
        // RoutingRuleWriter, resolving each part's family to a catalog item
        // by normalized name. Merged with the planned links (the planned
        // ones win — they carry the dialog-accurate embedded version label).
        if (routingRuleRepository is not null && catalog is not null)
        {
            await AugmentLinksFromRoutingRulesAsync(
                items, importedParentItemIds, routingRuleRepository, catalog, linksByParent, ct)
                .ConfigureAwait(false);
        }

        foreach (var pair in linksByParent)
        {
            ct.ThrowIfCancellationRequested();
            var parentId = pair.Key;
            var links = pair.Value;
            try
            {
                var written = await repository
                    .ReplaceForCurrentVersionAsync(parentId, links, ct)
                    .ConfigureAwait(false);
                SmartConLogger.Info(
                    $"Dependency links written: {written} (parent CatalogItemId={parentId})");
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"Dependency link write failed for parent {parentId}: {ex.GetType().Name}: {ex.Message}. " +
                    "[Action: sync этого эталона будет работать по имени (fallback); связи можно создать повторным импортом родителя]");
            }
        }
    }

    /// <summary>
    /// ADR-072 (plan item 5): derives parent→child links from the parent's
    /// stored routing rules (V34) and merges them into
    /// <paramref name="linksByParent"/> (dedup by child+kind — a planned
    /// link already present keeps its dialog-accurate version label).
    /// Unresolvable part families are NOT warnings: a skipped dependency
    /// (ADR-066 Skip default) legitimately has no catalog item — the
    /// routing editor surfaces it as a presence flag instead.
    /// </summary>
    private static async Task AugmentLinksFromRoutingRulesAsync(
        IReadOnlyList<FamilyBatchImportItem> items,
        IReadOnlyDictionary<string, string> importedParentItemIds,
        IFamilyRoutingRuleRepository routingRuleRepository,
        IFamilyCatalogProvider catalog,
        Dictionary<string, List<FamilyDependencyInfo>> linksByParent,
        CancellationToken ct)
    {
        var systemItemsByPath = items
            .Where(i => i.SystemSnapshot is not null)
            .GroupBy(i => i.FilePath, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var pair in importedParentItemIds)
        {
            ct.ThrowIfCancellationRequested();
            var parentId = pair.Value;
            if (!systemItemsByPath.ContainsKey(pair.Key))
                continue;

            IReadOnlyList<FamilyRoutingRuleInfo> rules;
            try
            {
                var read = await routingRuleRepository
                    .ReadForCurrentVersionAsync(parentId, ct)
                    .ConfigureAwait(false);
                rules = read.Rules;
            }
            catch (Exception ex)
            {
                SmartConLogger.Debug($"Routing-rule link augmentation read failed for {parentId}: {ex.Message}");
                continue;
            }
            if (rules.Count == 0)
                continue;

            if (!linksByParent.TryGetValue(parentId, out var links))
            {
                links = new List<FamilyDependencyInfo>();
                linksByParent.Add(parentId, links);
            }
            var seen = new HashSet<string>(
                links.Select(l => l.ChildCatalogItemId + "|" + l.Kind),
                StringComparer.OrdinalIgnoreCase);

            var partNames = rules
                .Where(r => r.PartName is not null && r.GroupKey != RoutingGroupKeys.ForManagerGroup(0))
                .Select(r => r.PartName!)
                .Distinct(StringComparer.Ordinal);
            foreach (var partName in partNames)
            {
                var separator = partName.IndexOf(':');
                if (separator <= 0)
                    continue;
                var familyName = partName.Substring(0, separator);
                var child = await catalog
                    .FindByNormalizedNameAsync(FamilyNameNormalizer.Normalize(familyName), "loadable", ct)
                    .ConfigureAwait(false);
                if (child is null)
                {
                    SmartConLogger.Debug(
                        $"Routing part '{partName}' has no catalog item — no link (presence flag in the routing editor)");
                    continue;
                }
                if (!seen.Add(child.Id + "|" + FamilyDependencyKind.Routing))
                    continue;

                links.Add(new FamilyDependencyInfo(
                    child.Id,
                    FamilyDependencyKind.Routing,
                    partName,
                    links.Count,
                    child.CurrentVersionLabel));
            }
        }
    }
}
