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
            $"[SystemImport.Extract] Awaiting extraction for {tasks.Count} .rvt task(s)...");

        var pendingSaves = new List<Task>();

        // Phase 27: snapshot-based extraction is pure C# and does NOT need
        // the Revit UI thread (no OpenDocumentFile). Only the legacy null-
        // snapshot path would need ExternalEvent, but production now always
        // provides a snapshot from Prepare. We iterate inline and fire saves
        // in parallel; no AwaitableEvent marshalling required.
        foreach (var task in tasks)
        {
            try
            {
                if (task.Snapshot is null)
                {
                    SmartConLogger.Warn(
                        $"[SystemImport.Extract] Snapshot is null for '{Path.GetFileName(task.ManagedRvtPath)}' " +
                        $"(CatalogItemId={task.CatalogItemId}) — cannot extract without re-open. " +
                        "[Action: check Prepare logs — snapshot extraction may have failed; re-import the category to fix]");
                    continue;
                }

                var extraction = SnapshotExtractionMapper.ToExtractionResult(
                    task.Snapshot, task.RevitMajorVersion);

                if (extraction.Success)
                {
                    var saveTask = SaveExtractionSafelyAsync(task, extraction);
                    pendingSaves.Add(saveTask);
                }
                else
                {
                    SmartConLogger.Warn(
                        $"[SystemImport.Extract] Snapshot extraction failed for '{Path.GetFileName(task.ManagedRvtPath)}': " +
                        $"{extraction.ErrorMessage} [Action: check snapshot mapper logs for details]");
                }
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"[SystemImport.Extract] Extraction exception for '{task.ManagedRvtPath}': {ex.Message} [Action: проверьте логи SmartCon; batch продолжит с другими категориями]");
            }
        }

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
            $"[SystemImport.Extract] ✓ Extraction phase complete ({pendingSaves.Count} file(s) saved) [from snapshot, no re-open]");
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
