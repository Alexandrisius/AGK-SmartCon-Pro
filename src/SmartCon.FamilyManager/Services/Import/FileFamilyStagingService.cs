using System.IO;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.Import;

public sealed class FileFamilyStagingService : IFileFamilyStagingService
{
    private readonly IFamilyManagerAwaitableEvent _awaitableEvent;
    private readonly IFamilyImportPreparationService _preparationService;
    private readonly IFamilyImportService _importService;

    public FileFamilyStagingService(
        IFamilyManagerAwaitableEvent awaitableEvent,
        IFamilyImportPreparationService preparationService,
        IFamilyImportService importService)
    {
        _awaitableEvent = awaitableEvent;
        _preparationService = preparationService;
        _importService = importService;
    }

    public Task CloseAllPreparedDocumentsAsync(CancellationToken ct)
        => _preparationService.CloseAllPreparedDocumentsAsync(ct);

    public Task<FamilyBatchImportItem> StageAsync(FamilyBatchImportItem item, CancellationToken ct)
    {
        if (item.FamilySource != "loadable" || item.LoadableSnapshot is null
            || item.FilePath.StartsWith("loadable://", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(item);
        }

        // Issue #126 / ADR-041: MakeActive imports NO file — it only
        // switches current_version_label on the matched catalog item.
        // Staging would SaveAs the held document into the precomputed
        // vN+1 path and leave an orphan managed file that no
        // catalog_versions row references.
        if (item.Action == FamilyBatchImportAction.MakeActive)
        {
            return Task.FromResult(item);
        }

        return _awaitableEvent.RaiseAsync(_ =>
        {
            string managedRfaPath;
            if (item.Action == FamilyBatchImportAction.OverwriteCurrent
                && !string.IsNullOrEmpty(item.ExistingCatalogItemId)
                && !string.IsNullOrEmpty(item.ExistingVersionLabel))
            {
                managedRfaPath = _importService.ComputeManagedFilePath(
                    item.ExistingCatalogItemId!,
                    item.ExistingVersionLabel!,
                    SafeFileName.SanitizeFileName(item.FileName),
                    ".rfa") ?? string.Empty;
                if (string.IsNullOrEmpty(managedRfaPath))
                {
                    SmartConLogger.Warn(
                        $"OverwriteCurrent staging for '{item.FileName}': ComputeManagedFilePath returned null " +
                        $"[Action: check active catalog DB is selected and pathResolver is configured]");
                    return item;
                }
            }
            else
            {
                if (string.IsNullOrEmpty(item.PrecomputedManagedPath))
                {
                    return item;
                }
                managedRfaPath = item.PrecomputedManagedPath!;
            }

            var stagedFileName = SafeFileName.SanitizeFileName(item.FileName);
            var managedDir = Path.GetDirectoryName(managedRfaPath);
            var managedName = Path.GetFileNameWithoutExtension(managedRfaPath);
            if (!string.IsNullOrEmpty(stagedFileName)
                && !string.IsNullOrEmpty(managedDir)
                && !string.Equals(managedName, stagedFileName, StringComparison.OrdinalIgnoreCase))
            {
                SmartConLogger.Warn(
                    $"Precomputed managed path filename '{managedName}' does not match item name '{item.FileName}' — " +
                    $"staging as '{stagedFileName}.rfa' so the loaded family gets the display name " +
                    $"[Action: если повторяется, проверьте что rename-триплет строки успел пересчитаться до старта импорта]");
                managedRfaPath = Path.Combine(managedDir, stagedFileName + ".rfa");
            }

            var parent = Path.GetDirectoryName(managedRfaPath);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            if (File.Exists(managedRfaPath))
            {
                File.SetAttributes(managedRfaPath, File.GetAttributes(managedRfaPath) & ~FileAttributes.ReadOnly);
                File.Delete(managedRfaPath);
            }

            var heldDoc = _preparationService.GetOpenedDocument(item.FilePath);
            if (heldDoc is null)
            {
                // For "nested://" rows there is no source file on disk — the
                // row will fail at import with file-not-found; the message
                // must not promise a copy/bake fallback that cannot exist.
                var fallbackNote = item.FilePath.StartsWith("nested://", StringComparison.OrdinalIgnoreCase)
                    ? "у вложенного семейства нет исходного файла — строка получит ошибку импорта"
                    : "import will fall back to copy/bake from source";
                SmartConLogger.Warn(
                    $"Held-open document not found for '{item.FileName}' (path='{item.FilePath}') — " +
                    $"{fallbackNote} [Action: check Prepare logs " +
                    "— document may have been closed early]");
                return item;
            }

            try
            {
                heldDoc.SaveAs(managedRfaPath, new SaveAsOptions { OverwriteExistingFile = true });
                File.SetAttributes(managedRfaPath, File.GetAttributes(managedRfaPath) | FileAttributes.ReadOnly);
                _preparationService.CloseAndRelease(item.FilePath);
                SmartConLogger.Info(
                    $"Staged loadable family '{item.FileName}' from held doc → '{managedRfaPath}' [no re-open]");
                return item with { FilePath = managedRfaPath };
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"SaveAs from held doc failed for '{item.FileName}': {ex.GetType().Name}: {ex.Message} — " +
                    "import will fall back to copy/bake from source [Action: проверьте логи Revit " +
                    "и что managed storage доступен для записи]");
                _preparationService.CloseAndRelease(item.FilePath);
                return item;
            }
        }, ct);
    }
}
