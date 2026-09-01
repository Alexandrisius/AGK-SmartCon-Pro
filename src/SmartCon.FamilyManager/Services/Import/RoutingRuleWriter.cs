using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.Import;

/// <summary>
/// ADR-072 World B (owner decision 2026-08-29): seeds the ITEM-level
/// routing link tables (<c>item_routing_rules</c> /
/// <c>item_routing_type_settings</c>, V37) from the item's FINAL snapshot —
/// but ONLY while the item carries no links yet. Routing is a catalog-family
/// link, not file content: a re-import never overwrites curated links (the
/// editor edits them in place). The current version is stamped
/// <c>routing_backfilled = 1</c> so the optional backfill task never
/// re-opens the file. Failures never roll back the import — sync falls back
/// to reading the mini (legacy path).
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
            ("Writer", nameof(RoutingRuleWriter)),
            ("Count", systemItems.Count));

        foreach (var item in systemItems)
        {
            ct.ThrowIfCancellationRequested();
            var parentId = importedParentItemIds[item.FilePath];
            try
            {
                if (await repository.HasAnyForItemAsync(parentId, ct).ConfigureAwait(false))
                {
                    SmartConLogger.Debug(
                        $"Item {parentId} already carries item-level routing links — " +
                        "reimport keeps the curated links (World B)");
                }
                else if (item.UnsubstitutedMiniRouting)
                {
                    // Audit M11: the snapshot routing is the slim mini state
                    // (reimport from mini + no stored rows to substitute) —
                    // seeding it would enshrine Segments-only/no-part rules
                    // as catalog truth and the next sync would converge user
                    // projects to the slim state. The item stays unseeded
                    // (sync's legacy fallback treats it as "no opinion").
                    SmartConLogger.Warn(
                        $"Item {parentId}: routing NOT seeded — the reimport comes from a slim mini-project " +
                        "and the catalog holds no stored routing rows to restore. " +
                        "[Action: реимпортируйте эталон из живого проекта (трассировка засеется полной) " +
                        "или настройте её во вкладке «Трассировка»]");
                }
                else
                {
                    var rules = new List<FamilyRoutingRuleInfo>();
                    var settings = new List<FamilyRoutingTypeSettings>();
                    foreach (var type in item.SystemSnapshot!.Types)
                    {
                        RoutingRuleRecordMapper.ToRecords(type, rules, settings);
                    }

                    // FHV21: segment rules are PER-VERSION content — they
                    // never enter the item-level link tables (the parallel
                    // SegmentRuleWriter persists them per version).
                    rules = rules
                        .Where(r => !SegmentRuleComposition.IsSegmentsGroup(r.GroupKey))
                        .ToList();

                    await repository
                        .ReplaceForItemAsync(parentId, rules, settings, ct)
                        .ConfigureAwait(false);
                    SmartConLogger.Info(
                        $"Routing links seeded: {rules.Count} rules + {settings.Count} settings " +
                        $"(parent CatalogItemId={parentId})");
                }

                await repository
                    .MarkCurrentVersionRoutingBackfilledAsync(parentId, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SmartConLogger.Warn(
                    $"Routing rule write failed for parent {parentId}: {ex.GetType().Name}: {ex.Message}. " +
                    "[Action: sync этого эталона прочитает трассировку из мини-проекта (legacy); " +
                    "ссылки можно засеять повторным импортом родителя]");
            }
        }
    }
}
