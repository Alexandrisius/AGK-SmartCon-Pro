using System.IO;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services;

/// <summary>
/// Single source of truth for "open staged .rvt → extract Type parameters → save to catalog"
/// across the entire system-family import surface (both ImportActiveFile Project flow and
/// ImportSystemFamily picker flow).
///
/// The implementation is identical to the previously-inlined helpers
/// <c>FamilyManagerMainViewModel.ExtractAttributesFromRvtsAsync</c> and
/// <c>FamilyManagerMainViewModel.ExtractSystemFamilyAttributesAsync</c>, which were
/// near-duplicates. Moving this logic into a service:
///
/// <list type="bullet">
///   <item>eliminates the duplicate code path (DRY);</item>
///   <item>centralises the await-extract-save-cleanup ordering guarantee;</item>
///   <item>keeps the awaitable-event race-condition fix in one place.</item>
/// </list>
/// </summary>
internal sealed class SystemFamilyAttributeExtractor : ISystemFamilyAttributeExtractor
{
    private readonly IFamilyManagerAwaitableEvent _awaitableEvent;
    private readonly ISystemFamilyAttributeExtractionService _extraction;
    private readonly IFamilyDataImportService _dataImportService;

    public SystemFamilyAttributeExtractor(
        IFamilyManagerAwaitableEvent awaitableEvent,
        ISystemFamilyAttributeExtractionService extraction,
        IFamilyDataImportService dataImportService)
    {
        _awaitableEvent = awaitableEvent;
        _extraction = extraction;
        _dataImportService = dataImportService;
    }

    public async Task ExtractAndSaveAsync(
        IReadOnlyList<SystemFamilyExtractionTask> tasks,
        CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("SystemFamilyAttr",
            ("Method", "ExtractAndSaveAsync"));
        if (tasks.Count == 0) return;

        SmartConLogger.Debug(
            $"[SystemImport.Extract] Awaiting extraction for {tasks.Count} .rvt task(s) via AwaitableEvent...");

        var pendingSaves = new List<Task>();

        await _awaitableEvent.RaiseAsync(_ =>
        {
            foreach (var task in tasks)
            {
                try
                {
                    if (!File.Exists(task.ManagedRvtPath))
                    {
                        SmartConLogger.Warn(
                            $"[SystemImport.Extract] Managed .rvt not found for extraction: {task.ManagedRvtPath} [Action: проверьте, что антивирус не удалил файл, или повторите импорт категории]");
                        continue;
                    }

                    var extraction = _extraction.ExtractFromRvt(
                        task.ManagedRvtPath, task.TypeNames);
                    if (extraction.Success)
                    {
                        // v2.0.0 hotfix: SaveExtractionResultAsync is already
                        // an async method that returns a Task. The previous
                        // implementation wrapped it in Task.Run, which (a)
                        // double-scheduled the work onto the thread pool,
                        // and (b) created a flaky race in unit tests where
                        // the second task's extraction appeared to be
                        // skipped when both saves hit the thread pool at
                        // the same time. Call the async method directly and
                        // let Task.WhenAll drive completion.
                        var saveTask = SaveExtractionSafelyAsync(task, extraction);
                        pendingSaves.Add(saveTask);
                    }
                    else
                    {
                        SmartConLogger.Warn(
                            $"[SystemImport.Extract] Extraction failed for '{Path.GetFileName(task.ManagedRvtPath)}': " +
                            $"{extraction.ErrorMessage} [Action: проверьте логи Revit; категория будет записана без extracted attributes]");
                    }
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn(
                        $"[SystemImport.Extract] Extraction exception for '{task.ManagedRvtPath}': {ex.Message} [Action: проверьте логи Revit; batch продолжит с другими категориями]");
                }
            }
        }, ct);

        if (pendingSaves.Count > 0)
        {
            SmartConLogger.Debug(
                $"[SystemImport.Extract] Waiting for {pendingSaves.Count} save(s)...");
            try
            {
                await Task.WhenAll(pendingSaves);
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"[SystemImport.Extract] One or more saves failed: {ex.Message} [Action: проверьте БД каталога; некоторые категории могут не иметь extracted attributes]");
            }
        }

        if (tasks.Count > 0)
        {
            SmartConLogger.Debug(
                $"[SystemImport.Extract] Extraction complete; managed .rvt files retained in catalog storage (I-16 immutable).");
        }

        SmartConLogger.Info(
            $"[SystemImport.Extract] ✓ Extraction phase complete ({pendingSaves.Count} file(s) saved)");
    }

    private async Task SaveExtractionSafelyAsync(
        SystemFamilyExtractionTask task,
        FamilyExtractionResult extraction)
    {
        try
        {
            await _dataImportService.SaveExtractionResultAsync(
                task.CatalogItemId, extraction, task.VersionId, task.FileId,
                CancellationToken.None).ConfigureAwait(false);
            SmartConLogger.Debug(
                $"[SystemImport.Extract] Saved extraction for '{Path.GetFileName(task.ManagedRvtPath)}': " +
                $"{extraction.Types.Count} types");
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"[SystemImport.Extract] SaveExtractionResult failed: {ex.Message} [Action: проверьте права на запись в БД каталога и целостность SQLite файла]");
        }
    }
}
