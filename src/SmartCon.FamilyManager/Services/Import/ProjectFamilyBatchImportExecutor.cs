using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Threading;

namespace SmartCon.FamilyManager.Services.Import;

public sealed class ProjectFamilyBatchImportExecutor : IFamilyBatchImportExecutor
{
    private readonly IProjectFamilyStagingService _staging;
    private readonly ISystemFamilyImportOrchestrator _systemFamilyImportOrchestrator;
    private readonly ISystemFamilyAttributeExtractor _systemFamilyAttributeExtractor;
    private readonly ILoadableFamilyImportOrchestrator _loadableFamilyImportOrchestrator;
    private readonly IFamilyVersionWriter _versionWriter;
    private readonly IStaleDetector _staleDetector;
    private readonly IFamilyCatalogProvider _catalog;
    private readonly LoadableAttributeExtractionHelper _extraction;
    private readonly int _revitVersion;

    public ProjectFamilyBatchImportExecutor(
        IProjectFamilyStagingService staging,
        ISystemFamilyImportOrchestrator systemFamilyImportOrchestrator,
        ISystemFamilyAttributeExtractor systemFamilyAttributeExtractor,
        ILoadableFamilyImportOrchestrator loadableFamilyImportOrchestrator,
        IFamilyDataImportService dataImportService,
        ISharedNestedFamilyRepository sharedNestedRepository,
        IFamilyVersionWriter versionWriter,
        IStaleDetector staleDetector,
        IFamilyCatalogProvider catalog,
        int revitVersion)
    {
        _staging = staging;
        _systemFamilyImportOrchestrator = systemFamilyImportOrchestrator;
        _systemFamilyAttributeExtractor = systemFamilyAttributeExtractor;
        _loadableFamilyImportOrchestrator = loadableFamilyImportOrchestrator;
        _versionWriter = versionWriter;
        _staleDetector = staleDetector;
        _catalog = catalog;
        _revitVersion = revitVersion;
        _extraction = new LoadableAttributeExtractionHelper(
            dataImportService, sharedNestedRepository, revitVersion);
    }

    public async Task<FamilyBatchImportExecutionResult> ExecuteAsync(
        IReadOnlyList<FamilyBatchImportItem> items,
        string? categoryId,
        IProgress<FamilyBatchImportProgress>? progress,
        PauseGate? pauseGate,
        CancellationToken ct)
    {
        using var _scope = SmartConLogger.BeginScope("BatchImport",
            ("Method", nameof(ExecuteAsync)),
            ("Count", items.Count));

        var success = 0;
        var skipped = 0;
        var errors = 0;
        var stopped = false;
        var importedLoadableItems = new List<FamilyBatchImportItem>();
        var loadableAttributeTasks = new List<LoadableFamilyAttributeTask>();

        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (pauseGate?.IsPaused == true)
                {
                    Report(progress, i, items.Count, items[i].FileName,
                        FamilyBatchImportPhase.Paused, null, null, success, skipped, errors);
                    await pauseGate.WaitWhilePausedAsync().ConfigureAwait(false);
                }
                if (ct.IsCancellationRequested)
                {
                    stopped = true;
                    break;
                }

                var item = items[i];
                if (item.Action == FamilyBatchImportAction.Skip)
                {
                    skipped++;
                    Report(progress, i, items.Count, item.FileName,
                        FamilyBatchImportPhase.Importing, FamilyBatchImportRowState.Skipped, null, success, skipped, errors);
                    continue;
                }

                try
                {
                    if (item.FamilySource == "system")
                    {
                        (success, skipped, errors) = await ProcessSystemItemAsync(
                            item, i, items.Count, progress, success, skipped, errors, ct)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        (success, skipped, errors) = await ProcessLoadableItemAsync(
                            item, i, items.Count, categoryId, progress,
                            success, skipped, errors,
                            importedLoadableItems, loadableAttributeTasks,
                            ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    stopped = true;
                    break;
                }
                catch (Exception ex)
                {
                    errors++;
                    SmartConLogger.Error(
                        $"Item '{item.FileName}' failed: {ex.GetType().Name}: {ex.Message}");
                    Report(progress, i, items.Count, item.FileName,
                        FamilyBatchImportPhase.Importing, FamilyBatchImportRowState.Error,
                        ex.Message, success, skipped, errors);
                }
            }

            if (!stopped && importedLoadableItems.Count > 0)
            {
                Report(progress, items.Count, items.Count, string.Empty,
                    FamilyBatchImportPhase.Finalizing, null, null, success, skipped, errors);
                try
                {
                    await LoadableMarkerLogic.WriteMarkersForImportedLoadablesAsync(
                        importedLoadableItems,
                        loadableAttributeTasks,
                        _versionWriter,
                        _revitVersion,
                        CancellationToken.None).ConfigureAwait(false);
                    // Selective invalidation: only the imported items' check
                    // results are outdated — other families keep their stale
                    // badges (a full InvalidateCache wiped them on every import).
                    _staleDetector.InvalidateItems(importedLoadableItems
                        .Select(i => i.PrecomputedCatalogItemId ?? i.ExistingCatalogItemId)
                        .Where(id => !string.IsNullOrEmpty(id))
                        .Select(id => id!)
                        .ToArray());
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn(
                        $"Version marker write failed: {ex.Message} [Action: семейства в проекте останутся без маркера, Проверить покажет stale до явной Загрузки]");
                }
            }
        }
        finally
        {
            try
            {
                await _staging
                    .CloseAllPreparedDocumentsAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"CloseAllPreparedDocumentsAsync failed: {ex.Message} [Action: проверьте, что в Revit не осталось открытых семейств; закройте их вручную]");
            }
        }

        SmartConLogger.Info(
            $"Finished: success={success}, skipped={skipped}, errors={errors}, stopped={stopped}");
        return new FamilyBatchImportExecutionResult(success, skipped, errors, stopped);
    }

    private async Task<(int Success, int Skipped, int Errors)> ProcessSystemItemAsync(
        FamilyBatchImportItem item,
        int index,
        int total,
        IProgress<FamilyBatchImportProgress>? progress,
        int success,
        int skipped,
        int errors,
        CancellationToken ct)
    {
        Report(progress, index, total, item.FileName,
            FamilyBatchImportPhase.Staging, null, null, success, skipped, errors);
        var staged = await _staging.StageSystemAsync(item, ct).ConfigureAwait(false);
        if (staged is null)
        {
            errors++;
            Report(progress, index, total, item.FileName,
                FamilyBatchImportPhase.Staging, FamilyBatchImportRowState.Error,
                "Staging failed — see log", success, skipped, errors);
            return (success, skipped, errors);
        }

        Report(progress, index, total, item.FileName,
            FamilyBatchImportPhase.Importing, null, null, success, skipped, errors);
        var sysResult = await _systemFamilyImportOrchestrator
            .ImportBatchItemsAsync(new[] { staged })
            .ConfigureAwait(false);

        if (sysResult.Success)
        {
            if (sysResult.ExtractionTasks.Count > 0)
            {
                Report(progress, index, total, item.FileName,
                    FamilyBatchImportPhase.Extracting, null, null, success, skipped, errors);
                await _systemFamilyAttributeExtractor
                    .ExtractAndSaveAsync(sysResult.ExtractionTasks, ct)
                    .ConfigureAwait(false);
            }
            success++;
            // Issue #104: the types in the active project ARE the source of
            // the just-stored catalog version — stamp them with the version
            // marker so the next Check does not flag them as stale.
            await WriteSystemTypeMarkersAsync(staged, sysResult, ct).ConfigureAwait(false);
            Report(progress, index, total, item.FileName,
                FamilyBatchImportPhase.Importing, FamilyBatchImportRowState.Success, null, success, skipped, errors);
        }
        else
        {
            errors++;
            Report(progress, index, total, item.FileName,
                FamilyBatchImportPhase.Importing, FamilyBatchImportRowState.Error,
                sysResult.Message ?? "Import failed", success, skipped, errors);
        }

        return (success, skipped, errors);
    }

    /// <summary>
    /// Issue #104: after a successful system import the active project's
    /// types receive the version marker of the just-stored catalog version
    /// (they are its authoritative source). Marker failures never roll back
    /// the import — an unmarked type is simply flagged stale by the next
    /// Check until it is explicitly synced.
    /// </summary>
    private async Task WriteSystemTypeMarkersAsync(
        FamilyBatchImportItem staged,
        SystemFamilyImportResult sysResult,
        CancellationToken ct)
    {
        try
        {
            var catalogItemId = sysResult.ExtractionTasks.Count > 0
                ? sysResult.ExtractionTasks[0].CatalogItemId
                : null;
            var sourceTypes = staged.SourceTypes;
            if (string.IsNullOrEmpty(catalogItemId) || sourceTypes is null || sourceTypes.Count == 0)
                return;

            var catalogItem = await _catalog.GetItemAsync(catalogItemId!, ct).ConfigureAwait(false);
            var versionLabel = catalogItem?.CurrentVersionLabel;

            var written = 0;
            foreach (var type in sourceTypes)
            {
                if (string.IsNullOrEmpty(type.UniqueId)) continue;
                try
                {
                    await _versionWriter.WriteSystemTypeMarkerAsync(
                        catalogItemId!, type.UniqueId, versionLabel, _revitVersion, ct)
                        .ConfigureAwait(false);
                    written++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn(
                        $"System type marker write failed for '{type.Name}' " +
                        $"(CatalogItemId={catalogItemId}): {ex.GetType().Name}: {ex.Message}. " +
                        "[Action: тип в проекте останется без маркера — Проверить покажет " +
                        "stale до явной Загрузки в проект]");
                }
            }

            if (written > 0)
            {
                SmartConLogger.Info(
                    $"System type markers written: {written}/{sourceTypes.Count} " +
                    $"(CatalogItemId={catalogItemId}, label={versionLabel ?? "<none>"}).");
                // Selective invalidation: only THIS item's check result is
                // outdated (it runs per system item in the batch — a full
                // InvalidateCache wiped the previous items' stale badges).
                _staleDetector.InvalidateItems(new[] { catalogItemId! });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"System type marker pass failed: {ex.Message} " +
                "[Action: типы в проекте останутся без маркеров — Проверить покажет " +
                "stale до явной Загрузки в проект]");
        }
    }

    private async Task<(int Success, int Skipped, int Errors)> ProcessLoadableItemAsync(
        FamilyBatchImportItem item,
        int index,
        int total,
        string? categoryId,
        IProgress<FamilyBatchImportProgress>? progress,
        int success,
        int skipped,
        int errors,
        List<FamilyBatchImportItem> importedLoadableItems,
        List<LoadableFamilyAttributeTask> loadableAttributeTasks,
        CancellationToken ct)
    {
        Report(progress, index, total, item.FileName,
            FamilyBatchImportPhase.Staging, null, null, success, skipped, errors);
        var staged = await _staging.StageLoadableAsync(item, ct).ConfigureAwait(false);
        if (staged is null)
        {
            errors++;
            Report(progress, index, total, item.FileName,
                FamilyBatchImportPhase.Staging, FamilyBatchImportRowState.Error,
                "Staging failed — see log", success, skipped, errors);
            return (success, skipped, errors);
        }

        Report(progress, index, total, item.FileName,
            FamilyBatchImportPhase.Importing, null, null, success, skipped, errors);
        var loadResult = await _loadableFamilyImportOrchestrator
            .ImportAndPersistTypesAsync(new[] { staged }, _revitVersion, categoryId, ct)
            .ConfigureAwait(false);

        if (loadResult.ImportedCount > 0)
        {
            success++;
            importedLoadableItems.Add(staged);
            loadableAttributeTasks.AddRange(loadResult.AttributeTasks);

            if (loadResult.AttributeTasks.Count > 0)
            {
                Report(progress, index, total, item.FileName,
                    FamilyBatchImportPhase.Extracting, null, null, success, skipped, errors);
                foreach (var task in loadResult.AttributeTasks)
                {
                    await _extraction.ExtractAsync(task, CancellationToken.None).ConfigureAwait(false);
                }
            }
            Report(progress, index, total, item.FileName,
                FamilyBatchImportPhase.Importing, FamilyBatchImportRowState.Success, null, success, skipped, errors);
        }
        else if (loadResult.SkippedCount > 0)
        {
            skipped++;
            Report(progress, index, total, item.FileName,
                FamilyBatchImportPhase.Importing, FamilyBatchImportRowState.Skipped, null, success, skipped, errors);
        }
        else
        {
            errors++;
            Report(progress, index, total, item.FileName,
                FamilyBatchImportPhase.Importing, FamilyBatchImportRowState.Error,
                loadResult.Message ?? "Import failed", success, skipped, errors);
        }

        return (success, skipped, errors);
    }

    private static void Report(
        IProgress<FamilyBatchImportProgress>? progress,
        int index, int total, string name,
        FamilyBatchImportPhase phase,
        FamilyBatchImportRowState? state,
        string? error,
        int success, int skipped, int errors)
    {
        progress?.Report(new FamilyBatchImportProgress(
            index, total, name, phase, state, error, success, skipped, errors));
    }
}
