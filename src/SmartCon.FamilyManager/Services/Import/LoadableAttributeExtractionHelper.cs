using System.IO;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.Import;

internal sealed class LoadableAttributeExtractionHelper
{
    private readonly IFamilyDataImportService _dataImportService;
    private readonly ISharedNestedFamilyRepository _sharedNestedRepository;
    private readonly int _revitVersion;

    public LoadableAttributeExtractionHelper(
        IFamilyDataImportService dataImportService,
        ISharedNestedFamilyRepository sharedNestedRepository,
        int revitVersion)
    {
        _dataImportService = dataImportService;
        _sharedNestedRepository = sharedNestedRepository;
        _revitVersion = revitVersion;
    }

    public async Task ExtractAsync(LoadableFamilyAttributeTask task, CancellationToken ct)
    {
        try
        {
            if (task.Snapshot is null)
            {
                SmartConLogger.Warn(
                    $"Snapshot is null for '{task.CatalogItemId}' — cannot extract attributes without re-open. " +
                    "[Action: check Prepare logs — snapshot extraction may have failed; re-import the family to fix]");
                return;
            }

            var extraction = SnapshotExtractionMapper.ToExtractionResult(task.Snapshot, _revitVersion);

            if (extraction.Success)
            {
                await _dataImportService.SaveExtractionResultAsync(
                    task.CatalogItemId, extraction, task.VersionId, task.FileId, ct).ConfigureAwait(false);
                SmartConLogger.Info(
                    $"Extracted {extraction.Types.Count} type(s) from snapshot for '{Path.GetFileName(task.ManagedRfaPath)}' " +
                    $"(CatalogItemId={task.CatalogItemId}) [no re-open]");

                await SaveSharedNestedNamesAsync(
                    task.CatalogItemId,
                    task.VersionId,
                    extraction.SharedNestedFamilyNamesSafe,
                    ct).ConfigureAwait(false);
            }
            else
            {
                SmartConLogger.Warn(
                    $"Snapshot extraction reported failure for '{task.CatalogItemId}': {extraction.ErrorMessage} " +
                    "[Action: check snapshot mapper logs for details]");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Extraction failed for '{task.CatalogItemId}': {ex.Message} [Action: проверьте, что .rfa не повреждён и Revit может открыть его вручную]");
        }
    }

    private async Task SaveSharedNestedNamesAsync(
        string catalogItemId,
        string? versionId,
        IReadOnlyList<string>? sharedNames,
        CancellationToken ct)
    {
        if (versionId is null || sharedNames is null || sharedNames.Count == 0)
        {
            return;
        }

        try
        {
            await _sharedNestedRepository
                .ReplaceForVersionAsync(catalogItemId, versionId, sharedNames, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Failed to persist shared-nested names for '{catalogItemId}' (v={versionId}): " +
                $"{ex.GetType().Name}: {ex.Message} " +
                "[Action: dialog will fall back to Revit API name only — re-import the family in Family Manager to refresh]");
        }
    }
}
