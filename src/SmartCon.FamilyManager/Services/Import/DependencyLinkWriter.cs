using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
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
        CancellationToken ct)
    {
        var children = items.Where(i => i.DependencyLinks is { Count: > 0 }).ToList();
        if (children.Count == 0)
        {
            return;
        }

        using var _scope = SmartConLogger.BeginScope("BatchImport",
            ("Method", nameof(WriteAsync)),
            ("Count", children.Count));

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

        foreach (var pair in plan.LinksByParent)
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
}
