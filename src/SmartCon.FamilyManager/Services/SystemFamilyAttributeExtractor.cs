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
                    if (!File.Exists(task.TempRvtPath))
                    {
                        SmartConLogger.Warn(
                            $"[SystemImport.Extract] Temp .rvt not found for extraction: {task.TempRvtPath}");
                        continue;
                    }

                    var extraction = _extraction.ExtractFromRvt(
                        task.TempRvtPath, task.TypeNames);
                    if (extraction.Success)
                    {
                        var saveTask = Task.Run(async () =>
                        {
                            try
                            {
                                await _dataImportService.SaveExtractionResultAsync(
                                    task.CatalogItemId, extraction, task.VersionId, task.FileId,
                                    CancellationToken.None);
                                SmartConLogger.Debug(
                                    $"[SystemImport.Extract] Saved extraction for '{Path.GetFileName(task.TempRvtPath)}': " +
                                    $"{extraction.Types.Count} types");
                            }
                            catch (Exception ex)
                            {
                                SmartConLogger.Warn(
                                    $"[SystemImport.Extract] SaveExtractionResult failed: {ex.Message}");
                            }
                        }, CancellationToken.None);
                        pendingSaves.Add(saveTask);
                    }
                    else
                    {
                        SmartConLogger.Warn(
                            $"[SystemImport.Extract] Extraction failed for '{Path.GetFileName(task.TempRvtPath)}': " +
                            $"{extraction.ErrorMessage}");
                    }
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn(
                        $"[SystemImport.Extract] Extraction exception for '{task.TempRvtPath}': {ex.Message}");
                }
            }
        }, ct);

        if (pendingSaves.Count > 0)
        {
            SmartConLogger.Debug(
                $"[SystemImport.Extract] Waiting for {pendingSaves.Count} save(s) before cleanup...");
            try
            {
                await Task.WhenAll(pendingSaves);
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"[SystemImport.Extract] One or more saves failed: {ex.Message}");
            }
        }

        foreach (var task in tasks)
        {
            try
            {
                if (File.Exists(task.TempRvtPath)) File.Delete(task.TempRvtPath);
                var metaPath = task.TempRvtPath + ".types.json";
                if (File.Exists(metaPath)) File.Delete(metaPath);
            }
            catch { }
        }

        SmartConLogger.Info(
            $"[SystemImport.Extract] ✓ Extraction phase complete ({pendingSaves.Count} file(s) saved)");
    }
}
