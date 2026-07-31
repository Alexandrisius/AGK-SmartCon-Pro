using System.IO;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services;

/// <summary>
/// Catalog-side orchestration of staged system-family imports. Owns the
/// "batch import + persist type descriptors + produce extraction task list"
/// step. Staging (.rvt creation) and attribute extraction live in their
/// own services (<see cref="ISystemFamilyIsolationProjectService"/>,
/// <see cref="ISystemFamilyAttributeExtractor"/>).
/// </summary>
internal sealed class SystemFamilyImportOrchestrator : ISystemFamilyImportOrchestrator
{
    private readonly IFamilyImportService _importService;
    private readonly IFamilyTypeRepository _typeRepository;

    public SystemFamilyImportOrchestrator(
        IFamilyImportService importService,
        IFamilyTypeRepository typeRepository)
    {
        _importService = importService;
        _typeRepository = typeRepository;
    }

    public async Task<SystemFamilyImportResult> ImportBatchItemsAsync(
        IReadOnlyList<FamilyBatchImportItem> items)
    {
        using var _scope = SmartConLogger.BeginScope("SystemImport",
            ("Method", "ImportBatchItemsAsync"),
            ("Count", items.Count));
        var totalImported = 0;
        var extractionTasks = new List<SystemFamilyExtractionTask>();

        try
        {
            var importResult = await _importService.ImportBatchAsync(
                items,
                categoryId: null,
                progress: null,
                CancellationToken.None).ConfigureAwait(false);

            totalImported = importResult.SuccessCount;

            foreach (var item in items)
            {
                // ADR-040: match by CatalogItemId first (precise key, works
                // for OverwriteCurrent where r.FileName has no extension but
                // item.FilePath does). Fall back to filename comparison for
                // edge cases where CatalogItemId is not set (e.g. UC-1 New
                // path where the id is allocated inside ImportFileAsync).
                var expectedCatalogItemId = item.ExistingCatalogItemId ?? item.PrecomputedCatalogItemId;
                var matchingResult = importResult.Results.FirstOrDefault(r =>
                    !string.IsNullOrEmpty(r.CatalogItemId)
                    && !string.IsNullOrEmpty(expectedCatalogItemId)
                    && string.Equals(r.CatalogItemId, expectedCatalogItemId, StringComparison.OrdinalIgnoreCase));
                if (matchingResult is null)
                {
                    matchingResult = importResult.Results.FirstOrDefault(r =>
                        !string.IsNullOrEmpty(r.FileName) &&
                        !string.IsNullOrEmpty(item.FileName) &&
                        string.Equals(
                            Path.GetFileNameWithoutExtension(r.FileName),
                            Path.GetFileNameWithoutExtension(item.FileName),
                            StringComparison.OrdinalIgnoreCase));
                }

                if (matchingResult is null)
                {
                    SmartConLogger.Warn(
                        $"No matching import result for system row '{item.FileName}' " +
                        $"(expectedCatalogItemId='{expectedCatalogItemId ?? "<null>"}', FilePath='{item.FilePath}') " +
                        $"[Action: проверьте, что ImportBatchAsync вернул CatalogItemId для этого item]");
                    continue;
                }
                if (!matchingResult.Success || string.IsNullOrEmpty(matchingResult.CatalogItemId))
                {
                    SmartConLogger.Warn(
                        $"Matching result for system row '{item.FileName}' is not successful " +
                        $"(Success={matchingResult.Success}, ErrorMessage='{matchingResult.ErrorMessage}', CatalogItemId='{matchingResult.CatalogItemId ?? "<null>"}') " +
                        $"[Action: проверьте логи ImportBatchAsync/OverwriteCurrentAsync для причины ошибки]");
                    continue;
                }

                var types = item.SourceTypes;
                if (types is null || types.Count == 0)
                {
                    SmartConLogger.Warn($"No source types provided for system row '{item.FileName}' [Action: проверьте, что выбранный проект содержит размещённые элементы этой категории]");
                    continue;
                }

                await SaveSystemTypesAsync(matchingResult.CatalogItemId!, types, matchingResult.VersionId, matchingResult.FileId).ConfigureAwait(false);
                SmartConLogger.Info($"Saved {types.Count} types for '{item.FileName}' (CatalogItemId={matchingResult.CatalogItemId})");

                extractionTasks.Add(new SystemFamilyExtractionTask(
                    matchingResult.CatalogItemId!,
                    matchingResult.ManagedFilePath ?? item.FilePath,
                    types.Select(t => t.Name).ToList(),
                    matchingResult.VersionId,
                    matchingResult.FileId,
                    Snapshot: item.SystemSnapshot,
                    RevitMajorVersion: item.RevitMajorVersion));
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"ImportBatchItemsAsync failed: {ex.Message}");
            return new SystemFamilyImportResult(false, ex.Message, extractionTasks, totalImported);
        }

        return new SystemFamilyImportResult(
            totalImported > 0,
            totalImported > 0 ? $"Imported {totalImported} system families" : "No families imported",
            extractionTasks,
            totalImported);
    }

    private async Task SaveSystemTypesAsync(string catalogItemId, IReadOnlyList<FamilySourceTypeInfo> types, string? versionId, string? fileId)
    {
        var descriptors = types.Select((t, i) => new FamilyTypeDescriptor(
            Id: Guid.NewGuid().ToString(),
            CatalogItemId: catalogItemId,
            Name: t.Name,
            SortOrder: i,
            VersionId: versionId,
            FileId: fileId,
            ExtractionRunId: null,
            UniqueId: t.UniqueId,
            // #183: the type identity is (family, name) — persisted into
            // family_types.family_name (V26).
            FamilyName: t.FamilyName)).ToList();

        // v2.0.0 (ADR-036): orchestrator replaces the entire type set for
        // the catalog item atomically. Pass the real versionId/fileId from
        // matchingResult so SyncTypesAsync can record the FK columns on the
        // new family_types rows. Pass runId="no-run" because system-family
        // project imports do not produce a family_data_import_runs row for
        // the type registration step (only attribute extraction creates a run).
        await _typeRepository.SyncTypesAsync(catalogItemId, versionId, fileId, runId: "no-run", descriptors).ConfigureAwait(false);
    }
}

