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
        int revitVersion)
    {
        _staging = staging;
        _systemFamilyImportOrchestrator = systemFamilyImportOrchestrator;
        _systemFamilyAttributeExtractor = systemFamilyAttributeExtractor;
        _loadableFamilyImportOrchestrator = loadableFamilyImportOrchestrator;
        _versionWriter = versionWriter;
        _staleDetector = staleDetector;
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
                    _staleDetector.InvalidateCache();
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
