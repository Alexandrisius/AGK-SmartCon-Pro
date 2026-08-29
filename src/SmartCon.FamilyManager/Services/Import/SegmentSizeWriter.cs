using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.Import;

/// <summary>
/// ADR-072 Phase 3: persists the segment size tables of imported system
/// MEPCurve items (V36) so the routing editor can offer the segment
/// nominal diameters as min/max rule criteria — the same list the Revit
/// routing dialog shows. Runs right after <c>RoutingRuleWriter</c> in both
/// batch executors; failures never roll back the import (the editor falls
/// back to hidden size pickers, the segment-sizes-v1 actualization
/// backfills later).
/// </summary>
internal static class SegmentSizeWriter
{
    public static async Task WriteAsync(
        IReadOnlyList<FamilyBatchImportItem> items,
        IReadOnlyDictionary<string, string> importedParentItemIds,
        ISegmentSizeRepository repository,
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
                var sizes = new List<SegmentSizeRecord>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var type in item.SystemSnapshot!.Types)
                {
                    if (type.Segments is null)
                        continue;
                    var order = 0;
                    foreach (var segment in type.Segments)
                    {
                        foreach (var size in segment.Sizes)
                        {
                            var key = segment.Name + "|" +
                                size.NominalDiameter.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
                            if (!seen.Add(key))
                                continue;
                            sizes.Add(new SegmentSizeRecord(
                                segment.Name,
                                size.NominalDiameter,
                                size.InnerDiameter,
                                size.OuterDiameter,
                                size.UsedInSizeLists,
                                size.UsedInSizing,
                                order++));
                        }
                    }
                }

                await repository.ReplaceForCurrentVersionAsync(parentId, sizes, ct).ConfigureAwait(false);
                SmartConLogger.Info(
                    $"Segment sizes written: {sizes.Count} (parent CatalogItemId={parentId})");
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"Segment size write failed for parent {parentId}: {ex.GetType().Name}: {ex.Message}. " +
                    "[Action: редактор трассировки скроет списки размеров; задача segment-sizes-v1 дозаполнит при «Обновить базу»]");
            }
        }
    }
}
