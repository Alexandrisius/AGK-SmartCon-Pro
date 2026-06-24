using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Issue #84 / Phase 24 (ADR-030): pure helper that re-syncs the
/// <c>SmartCon_FamilyVersion_v1</c> ExtensibleStorage marker onto every
/// loadable <c>Family</c> element in the active Revit project that just
/// landed in the catalog via "Импорт активного файла" / "Импорт выделенных
/// элементов" (see <c>ProcessProjectImportAsync</c>).
/// <para>
/// Without this step the next "Проверить" (Check) immediately flags every
/// freshly-imported loadable as stale (<see cref="StaleReason.NoEntityStorage"/>),
/// even though the in-project family IS the authoritative vN+1 source for
/// the new catalog row. The marker version written here equals the version
/// label the catalog just stored — <c>v1</c> for a new row, <c>vN+1</c> for a
/// re-import. The <c>Family</c> element bytes themselves are NOT changed;
/// only its ES marker is rewritten.
/// </para>
/// <para>
/// System families (<c>FamilySource == "system"</c>) are out of scope by
/// design — there is no <c>Family</c> element in the project for them
/// (OST_PipeCurves etc. are <c>MEPCurve</c> / <c>Wall</c> in Revit), and
/// the caller filters them out upstream so this helper never sees one.
/// </para>
/// <para>
/// Marker write failure for a single family is logged as
/// <see cref="SmartConLogger.Warn(string)"/> with an <c>[Action: …]</c> hint
/// and counted in <see cref="MarkerWriteSummary.FailedCount"/>. The caller
/// must NOT roll back the catalog import — the catalog has already accepted
/// the row. The family simply stays without a marker until the user
/// explicitly runs "Загрузить в проект", at which point it gets a marker
/// and the next Check re-evaluates.
/// </para>
/// <para>
/// Pure logic — no Revit API in the signature. Tests construct a fake
/// <see cref="IFamilyVersionWriter"/> and assert call counts + arguments.
/// </para>
/// </summary>
public static class LoadableMarkerLogic
{
    /// <summary>
    /// Aggregate result of a marker-write pass. <see cref="Total"/> equals
    /// <c>attributeTasks.Count</c>; the three counters sum to <c>Total</c>.
    /// </summary>
    public sealed record MarkerWriteSummary(
        int SuccessCount,
        int SkippedCount,
        int FailedCount,
        int Total);

    /// <summary>
    /// Iterate every <paramref name="attributeTasks"/> entry, locate the
    /// matching <see cref="FamilyBatchImportItem"/> by
    /// <c>PrecomputedCatalogItemId</c>, and invoke
    /// <see cref="IFamilyVersionWriter.WriteVersionMarkerAsync"/> with the
    /// <c>vN+1</c> / <c>v1</c> label the catalog just stored.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// Any reference argument is null.
    /// </exception>
    public static async Task<MarkerWriteSummary> WriteMarkersForImportedLoadablesAsync(
        IReadOnlyList<FamilyBatchImportItem> loadableItems,
        IReadOnlyList<LoadableFamilyAttributeTask> attributeTasks,
        IFamilyVersionWriter versionWriter,
        int targetRevit,
        CancellationToken ct)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(loadableItems);
        ArgumentNullException.ThrowIfNull(attributeTasks);
        ArgumentNullException.ThrowIfNull(versionWriter);
#else
        if (loadableItems is null) throw new ArgumentNullException(nameof(loadableItems));
        if (attributeTasks is null) throw new ArgumentNullException(nameof(attributeTasks));
        if (versionWriter is null) throw new ArgumentNullException(nameof(versionWriter));
#endif

        if (attributeTasks.Count == 0)
        {
            return new MarkerWriteSummary(SuccessCount: 0, SkippedCount: 0, FailedCount: 0, Total: 0);
        }

        using var _scope = SmartConLogger.BeginScope(
            "FMImport",
            ("Method", nameof(WriteMarkersForImportedLoadablesAsync)),
            ("Count", attributeTasks.Count));

        var successCount = 0;
        var skipCount = 0;
        var failCount = 0;

        foreach (var task in attributeTasks)
        {
            if (task is null || string.IsNullOrEmpty(task.CatalogItemId))
            {
                skipCount++;
                continue;
            }

            var item = loadableItems.FirstOrDefault(i =>
                i is not null &&
                !string.IsNullOrEmpty(i.PrecomputedCatalogItemId) &&
                string.Equals(i.PrecomputedCatalogItemId, task.CatalogItemId, StringComparison.Ordinal));
            if (item is null)
            {
                SmartConLogger.Debug(
                    $"WriteMarkers: no matching batch item for task.CatalogItemId='{task.CatalogItemId}', skipping marker write.");
                skipCount++;
                continue;
            }

            var familyName = item.FileName;
            var versionLabel = item.PrecomputedVersionLabel;

            try
            {
                await versionWriter
                    .WriteVersionMarkerAsync(task.CatalogItemId, familyName, versionLabel, targetRevit, ct)
                    .ConfigureAwait(false);
                successCount++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failCount++;
                SmartConLogger.Warn(
                    $"WriteVersionMarkerAsync failed for '{familyName}' (CatalogItemId={task.CatalogItemId}): " +
                    $"{ex.GetType().Name}: {ex.Message}. " +
                    "[Action: семейство в проекте останется без маркера, " +
                    "Проверить покажет stale до явной Загрузки]");
            }
        }

        SmartConLogger.Info(
            $"Loadable marker write: success={successCount}, skipped={skipCount}, failed={failCount} (total={attributeTasks.Count}).");

        return new MarkerWriteSummary(
            SuccessCount: successCount,
            SkippedCount: skipCount,
            FailedCount: failCount,
            Total: attributeTasks.Count);
    }
}
