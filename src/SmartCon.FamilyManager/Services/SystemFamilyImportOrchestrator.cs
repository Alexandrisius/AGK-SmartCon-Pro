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
                var matchingResult = importResult.Results.FirstOrDefault(r =>
                    !string.IsNullOrEmpty(r.FileName) &&
                    string.Equals(Path.GetFileName(item.FilePath), r.FileName, StringComparison.OrdinalIgnoreCase));

                if (matchingResult is null) continue;
                if (!matchingResult.Success || string.IsNullOrEmpty(matchingResult.CatalogItemId))
                    continue;

                var types = item.SourceTypes;
                if (types is null || types.Count == 0)
                {
                    SmartConLogger.Warn($"No source types provided for system row '{item.FileName}'");
                    continue;
                }

                await SaveSystemTypesAsync(matchingResult.CatalogItemId!, types, matchingResult.VersionId, matchingResult.FileId).ConfigureAwait(false);
                SmartConLogger.Info($"Saved {types.Count} types for '{item.FileName}' (CatalogItemId={matchingResult.CatalogItemId})");

                extractionTasks.Add(new SystemFamilyExtractionTask(
                    matchingResult.CatalogItemId!,
                    matchingResult.ManagedFilePath ?? item.FilePath,
                    types.Select(t => t.Name).ToList(),
                    matchingResult.VersionId,
                    matchingResult.FileId));
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
            UniqueId: t.UniqueId)).ToList();

        await _typeRepository.SaveTypesAsync(catalogItemId, descriptors).ConfigureAwait(false);
    }
}

