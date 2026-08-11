using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Threading;

namespace SmartCon.FamilyManager.Services.Import;

public sealed class FileFamilyBatchImportExecutor : IFamilyBatchImportExecutor
{
    private readonly IFileFamilyStagingService _staging;
    private readonly IFamilyImportService _importService;
    private readonly IFamilyDependencyRepository _familyDependencyRepository;
    private readonly LoadableAttributeExtractionHelper _extraction;

    public FileFamilyBatchImportExecutor(
        IFileFamilyStagingService staging,
        IFamilyImportService importService,
        IFamilyDependencyRepository familyDependencyRepository,
        IFamilyDataImportService dataImportService,
        ISharedNestedFamilyRepository sharedNestedRepository,
        int revitVersion)
    {
        _staging = staging;
        _importService = importService;
        _familyDependencyRepository = familyDependencyRepository;
        _extraction = new LoadableAttributeExtractionHelper(
            dataImportService, sharedNestedRepository, revitVersion);
    }

    public async Task<FamilyBatchImportExecutionResult> ExecuteAsync(
        IReadOnlyList<FamilyBatchImportItem> items,
        string? categoryId,
        IProgress<FamilyBatchImportProgress>? progress,
        PauseGate? pauseGate,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? externalParentItemIds = null)
    {
        using var _scope = SmartConLogger.BeginScope("BatchImport",
            ("Method", nameof(ExecuteAsync)),
            ("Count", items.Count));

        var success = 0;
        var skipped = 0;
        var errors = 0;
        var stopped = false;
        // ADR-066 (E2, #209): dependency link tracking — original dialog
        // paths of imported rows and the parent map (original path →
        // catalog item id) for the post-loop link write. UC-2 seeds the map
        // with parents imported outside this executor (active-family path).
        var importedLoadableOriginalPaths = new List<string>();
        var importedParentItemIds = new Dictionary<string, string>(StringComparer.Ordinal);
        if (externalParentItemIds is not null)
        {
            foreach (var pair in externalParentItemIds)
            {
                importedParentItemIds[pair.Key] = pair.Value;
            }
        }

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
                    Report(progress, i, items.Count, item.FileName,
                        FamilyBatchImportPhase.Staging, null, null, success, skipped, errors);
                    var staged = await _staging.StageAsync(item, ct).ConfigureAwait(false);

                    Report(progress, i, items.Count, item.FileName,
                        FamilyBatchImportPhase.Importing, null, null, success, skipped, errors);
                    var batchResult = await _importService
                        .ImportBatchAsync(new[] { staged }, categoryId, null, ct)
                        .ConfigureAwait(false);
                    var r = batchResult.Results.Count > 0 ? batchResult.Results[0] : null;

                    if (r is not null && r.Success && !r.WasSkipped)
                    {
                        success++;
                        importedLoadableOriginalPaths.Add(item.FilePath);
                        if (!string.IsNullOrEmpty(r.CatalogItemId))
                        {
                            importedParentItemIds[item.FilePath] = r.CatalogItemId!;
                        }
                        if (r.CatalogItemId is not null && staged.LoadableSnapshot is not null)
                        {
                            Report(progress, i, items.Count, item.FileName,
                                FamilyBatchImportPhase.Extracting, null, null, success, skipped, errors);
                            await _extraction.ExtractAsync(
                                new LoadableFamilyAttributeTask(
                                    r.CatalogItemId,
                                    r.ManagedFilePath ?? staged.FilePath,
                                    r.VersionId,
                                    r.FileId,
                                    staged.LoadableSnapshot),
                                CancellationToken.None).ConfigureAwait(false);
                        }
                        Report(progress, i, items.Count, item.FileName,
                            FamilyBatchImportPhase.Importing, FamilyBatchImportRowState.Success, null, success, skipped, errors);
                    }
                    else if (r is not null && r.WasSkipped)
                    {
                        skipped++;
                        Report(progress, i, items.Count, item.FileName,
                            FamilyBatchImportPhase.Importing, FamilyBatchImportRowState.Skipped, null, success, skipped, errors);
                    }
                    else
                    {
                        errors++;
                        Report(progress, i, items.Count, item.FileName,
                            FamilyBatchImportPhase.Importing, FamilyBatchImportRowState.Error,
                            r?.ErrorMessage ?? "Import failed", success, skipped, errors);
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
            if (!stopped)
            {
                await DependencyLinkWriter.WriteAsync(
                        items, importedParentItemIds, importedLoadableOriginalPaths,
                        _familyDependencyRepository, ct)
                    .ConfigureAwait(false);
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
