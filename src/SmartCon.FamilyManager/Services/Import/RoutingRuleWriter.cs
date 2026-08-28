using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.Import;

/// <summary>
/// ADR-072 (plan item 2): persists the routing rules of imported system
/// MEPCurve items to <c>family_routing_rules</c> /
/// <c>family_routing_type_settings</c> (V34) after the main import loop.
/// The records are built from the item's FINAL snapshot — the live
/// project's full routing for a live import, the DB-substituted routing
/// for a reimport from a slim mini (idempotent rewrite of the same data),
/// or the legacy mini's full routing for a pre-V34 reimport (self-heals
/// the version onto routing-as-data). Failures never roll back the import
/// — sync falls back to reading the mini (legacy path, plan item 3).
/// </summary>
internal static class RoutingRuleWriter
{
    public static async Task WriteAsync(
        IReadOnlyList<FamilyBatchImportItem> items,
        IReadOnlyDictionary<string, string> importedParentItemIds,
        IFamilyRoutingRuleRepository repository,
        CancellationToken ct)
    {
        var systemItems = items
            .Where(i => i.SystemSnapshot is not null
                && importedParentItemIds.ContainsKey(i.FilePath))
            .ToList();
        if (systemItems.Count == 0)
        {
            return;
        }

        using var _scope = SmartConLogger.BeginScope("BatchImport",
            ("Method", nameof(WriteAsync)),
            ("Count", systemItems.Count));

        foreach (var item in systemItems)
        {
            ct.ThrowIfCancellationRequested();
            var parentId = importedParentItemIds[item.FilePath];
            try
            {
                var rules = new List<FamilyRoutingRuleInfo>();
                var settings = new List<FamilyRoutingTypeSettings>();
                foreach (var type in item.SystemSnapshot!.Types)
                {
                    RoutingRuleRecordMapper.ToRecords(type, rules, settings);
                }

                await repository
                    .ReplaceForCurrentVersionAsync(parentId, rules, settings, ct)
                    .ConfigureAwait(false);
                SmartConLogger.Info(
                    $"Routing rules written: {rules.Count} rules + {settings.Count} settings " +
                    $"(parent CatalogItemId={parentId})");
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"Routing rule write failed for parent {parentId}: {ex.GetType().Name}: {ex.Message}. " +
                    "[Action: sync этого эталона прочитает трассировку из мини-проекта (legacy); " +
                    "правила можно записать повторным импортом родителя]");
            }
        }
    }
}
