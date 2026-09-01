using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.Import;

/// <summary>
/// FHV21 (owner decision 2026-09-01): persists the segment routing rules of
/// imported system pipe items into the PER-VERSION store
/// (<c>family_segment_rules</c>, V38) — the segment configuration is
/// mini-project content, versioned like the segment size tables (every
/// imported version gets its own rows, so a later rollback restores them).
/// The source is the type's Segments-group routing rules of the FINAL
/// snapshot (after the fitting-only DB substitution — the mini keeps its
/// own segment rules). Runs in both batch executors next to
/// <c>SegmentSizeWriter</c>; failures never roll back the import (readers
/// fall back to the stored Segments rows, segment-rules-v1 backfills later).
/// </summary>
internal static class SegmentRuleWriter
{
    public static async Task WriteAsync(
        IReadOnlyList<FamilyBatchImportItem> items,
        IReadOnlyDictionary<string, string> importedParentItemIds,
        ISegmentRuleRepository repository,
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
            ("Writer", nameof(SegmentRuleWriter)),
            ("Count", systemItems.Count));

        foreach (var item in systemItems)
        {
            ct.ThrowIfCancellationRequested();
            var parentId = importedParentItemIds[item.FilePath];
            try
            {
                // Single source: the mini's own Segments routing rules
                // (after the fitting-only DB substitution).
                var rules = SegmentRuleComposition.FromSnapshot(item.SystemSnapshot!);

                await repository.ReplaceForCurrentVersionAsync(parentId, rules, ct).ConfigureAwait(false);
                SmartConLogger.Info(
                    $"Segment rules written: {rules.Count} (parent CatalogItemId={parentId})");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SmartConLogger.Warn(
                    $"Segment rule write failed for parent {parentId}: {ex.GetType().Name}: {ex.Message}. " +
                    "[Action: читатели трассировки используют хранимые строки Segments (legacy); " +
                    "задача segment-rules-v1 дозаполнит при «Обновить базу»]");
            }
        }
    }
}
