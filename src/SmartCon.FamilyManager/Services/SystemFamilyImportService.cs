using System.IO;
using System.Text.Json;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services;

internal sealed class SystemFamilyImportService : ISystemFamilyImportService
{
    private readonly ISystemFamilyRevitOperations _revitOps;
    private readonly IFamilyImportService _importService;
    private readonly IFamilyCatalogProvider _catalogProvider;
    private readonly IFamilyTypeRepository _typeRepository;

    public SystemFamilyImportService(
        ISystemFamilyRevitOperations revitOps,
        IFamilyImportService importService,
        IFamilyCatalogProvider catalogProvider,
        IFamilyTypeRepository typeRepository)
    {
        _revitOps = revitOps;
        _importService = importService;
        _catalogProvider = catalogProvider;
        _typeRepository = typeRepository;
    }

    public IReadOnlyList<SystemFamilyPendingImport> PickAndPrepare()
    {
        var selectedTypes = _revitOps.PickSystemTypes();
        if (selectedTypes.Count == 0)
            return [];

        SmartConLogger.Info($"[SystemImport] Selected {selectedTypes.Count} system types");

        var result = new List<SystemFamilyPendingImport>();

        foreach (var group in selectedTypes.GroupBy(t => t.CategoryName))
        {
            var categoryName = group.Key;
            var types = group.ToList();
            var uniqueIds = types.Select(t => t.UniqueId).ToList();

            SmartConLogger.Info($"[SystemImport] Creating temp .rvt for '{categoryName}' with {types.Count} types");

            var createResult = _revitOps.CreateCleanProjectWithTypes(uniqueIds);
            if (!createResult.Success || string.IsNullOrEmpty(createResult.FilePath))
            {
                SmartConLogger.Warn($"[SystemImport] Failed to create temp .rvt for '{categoryName}': {createResult.Error}");
                continue;
            }

            try
            {
                var metaPath = createResult.FilePath + ".types.json";
                var typeNames = types.Select(t => t.Name).ToList();
                File.WriteAllText(metaPath, JsonSerializer.Serialize(typeNames));
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn($"[SystemImport] Failed to write sidecar meta for '{categoryName}': {ex.Message}");
            }

            result.Add(new SystemFamilyPendingImport(categoryName, types, createResult.FilePath!));
        }

        return result;
    }

    public async Task<SystemFamilyImportResult> ImportBatchItemsAsync(IReadOnlyList<FamilyBatchImportItem> items)
    {
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
                if (!matchingResult.Success || matchingResult.WasSkippedAsDuplicate || string.IsNullOrEmpty(matchingResult.CatalogItemId))
                    continue;

                var types = LoadTypesFromSidecar(item.FilePath);
                if (types.Count == 0)
                {
                    SmartConLogger.Warn($"[SystemImport] No types for '{item.FileName}'");
                }
                else
                {
                    await SaveSystemTypesAsync(matchingResult.CatalogItemId!, types, matchingResult.VersionId, matchingResult.FileId).ConfigureAwait(false);
                    SmartConLogger.Info($"[SystemImport] Saved {types.Count} types for '{item.FileName}' (CatalogItemId={matchingResult.CatalogItemId})");
                }

                extractionTasks.Add(new SystemFamilyExtractionTask(
                    matchingResult.CatalogItemId!,
                    item.FilePath,
                    types.Select(t => t.Name).ToList(),
                    matchingResult.VersionId,
                    matchingResult.FileId));
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"[SystemImport] ImportBatchItemsAsync failed: {ex.Message}");
            return new SystemFamilyImportResult(false, ex.Message, extractionTasks, totalImported);
        }

        return new SystemFamilyImportResult(
            totalImported > 0,
            totalImported > 0 ? $"Imported {totalImported} system families" : "No families imported",
            extractionTasks,
            totalImported);
    }

    private async Task SaveSystemTypesAsync(string catalogItemId, IReadOnlyList<SelectedSystemType> types, string? versionId, string? fileId)
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

    private static IReadOnlyList<SelectedSystemType> LoadTypesFromSidecar(string tempRvtPath)
    {
        try
        {
            var metaPath = tempRvtPath + ".types.json";
            if (!File.Exists(metaPath)) return [];

            var typeNames = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(metaPath));
            if (typeNames is null) return [];

            return typeNames
                .Select((name, i) => new SelectedSystemType($"temp-{i}", name, "Unknown"))
                .ToList();
        }
        catch
        {
            return [];
        }
    }
}
