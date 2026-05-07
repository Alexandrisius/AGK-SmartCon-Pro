using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed class FamilyDataImportService : IFamilyDataImportService
{
    private readonly IFamilyDataImportRunRepository _runRepository;
    private readonly IAttributeValueRepository _valueRepository;
    private readonly ICategoryAttributeBindingService _bindingService;
    private readonly IFamilyTypeRepository _typeRepository;
    private readonly IAttributeDefinitionRepository _attributeDefRepository;
    private readonly IFamilyCatalogProvider _catalogProvider;
    private readonly IFamilyFileResolver _fileResolver;

    public FamilyDataImportService(
        IFamilyDataImportRunRepository runRepository,
        IAttributeValueRepository valueRepository,
        ICategoryAttributeBindingService bindingService,
        IFamilyTypeRepository typeRepository,
        IAttributeDefinitionRepository attributeDefRepository,
        IFamilyCatalogProvider catalogProvider,
        IFamilyFileResolver fileResolver)
    {
        _runRepository = runRepository;
        _valueRepository = valueRepository;
        _bindingService = bindingService;
        _typeRepository = typeRepository;
        _attributeDefRepository = attributeDefRepository;
        _catalogProvider = catalogProvider;
        _fileResolver = fileResolver;
    }

    public async Task<FamilyDataImportResult> ImportDataAsync(string catalogItemId, CancellationToken ct = default)
    {
        var item = await _catalogProvider.GetItemAsync(catalogItemId, ct);
        if (item is null)
            return new FamilyDataImportResult(false, null, 0, 0, 0, "Catalog item not found");

        var effectiveAttrs = await _bindingService.GetEffectiveAttributesAsync(item.CategoryId, ct);
        var paramNames = effectiveAttrs
            .Where(a => a.IsEnabled)
            .Select(a => a.Name)
            .ToList()
            .AsReadOnly();

        var resolved = await _fileResolver.ResolveForLoadAsync(catalogItemId, 0, ct);
        if (string.IsNullOrEmpty(resolved.AbsolutePath))
            return new FamilyDataImportResult(false, null, 0, 0, 0, "Family file not found");

        return new FamilyDataImportResult(false, null, 0, 0, 0,
            "Use SaveExtractionResultAsync from ExternalEvent handler");
    }

    public async Task<FamilyExtractionPrepareResult> PrepareExtractionAsync(
        string catalogItemId, int targetRevitVersion, CancellationToken ct = default)
    {
        var item = await _catalogProvider.GetItemAsync(catalogItemId, ct);
        if (item is null)
            return new FamilyExtractionPrepareResult(false, null, null, [], "Catalog item not found");

        var resolved = await _fileResolver.ResolveForLoadAsync(catalogItemId, targetRevitVersion, ct);
        if (string.IsNullOrEmpty(resolved.AbsolutePath))
            return new FamilyExtractionPrepareResult(false, item, null, [], "Family file not found");

        var effectiveAttrs = await _bindingService.GetEffectiveAttributesAsync(item.CategoryId, ct);
        var allAttrs = await _attributeDefRepository.GetAllAsync(ct);
        var activeAttrIds = allAttrs.Where(a => a.IsActive).Select(a => a.Id).ToHashSet();
        var paramNames = effectiveAttrs
            .Where(a => a.IsEnabled && activeAttrIds.Contains(a.AttributeId))
            .Select(a => a.Name)
            .ToList()
            .AsReadOnly();

        return new FamilyExtractionPrepareResult(true, item, resolved.AbsolutePath, paramNames, null);
    }

    public async Task<FamilyDataImportResult> SaveExtractionResultAsync(
        string catalogItemId,
        FamilyExtractionResult extractionResult,
        string? versionId,
        string? fileId,
        CancellationToken ct = default)
    {
        var runId = Guid.NewGuid().ToString();
        var startedAt = DateTimeOffset.UtcNow;

        var run = new FamilyDataImportRun(
            runId,
            catalogItemId,
            versionId,
            fileId,
            null,
            extractionResult.RevitMajorVersion,
            FamilyDataImportStatus.Succeeded,
            extractionResult.Types.Count,
            startedAt,
            null,
            null);

        await _runRepository.CreateRunAsync(run, ct);

        var types = new List<FamilyTypeDescriptor>();
        for (var i = 0; i < extractionResult.Types.Count; i++)
        {
            var t = extractionResult.Types[i];
            types.Add(new FamilyTypeDescriptor(
                Guid.NewGuid().ToString(),
                catalogItemId,
                t.TypeName,
                t.SortOrder,
                versionId,
                fileId,
                runId));
        }

        if (types.Count > 0)
            await _typeRepository.SaveTypesForRunAsync(catalogItemId, versionId, fileId, runId, types, ct);

        var allAttrs = await _attributeDefRepository.GetAllAsync(ct);
        var attrByName = allAttrs.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);

        var values = new List<ExtractedAttributeValue>();
        foreach (var typeData in extractionResult.Types)
        {
            var typeRecord = types.FirstOrDefault(t => t.Name == typeData.TypeName);
            foreach (var val in typeData.Values)
            {
                attrByName.TryGetValue(val.ParameterName, out var attrDef);
                values.Add(new ExtractedAttributeValue(
                    Guid.NewGuid().ToString(),
                    catalogItemId,
                    versionId,
                    fileId,
                    typeRecord?.Id,
                    attrDef?.Id ?? val.ParameterName,
                    null,
                    val.ParameterName,
                    val.ParameterScope,
                    val.StorageType,
                    val.ValueText,
                    val.ValueRaw,
                    val.ValueNumber,
                    val.UnitTypeId,
                    val.Status,
                    val.Message,
                    runId,
                    DateTimeOffset.UtcNow));
            }
        }

        if (values.Count > 0)
            await _valueRepository.ReplaceSnapshotAsync(catalogItemId, versionId, runId, values, ct);

        var foundCount = values.Count(v => v.Status == AttributeValueStatus.Found);
        var missingCount = values.Count - foundCount;

        var status = extractionResult.Success
            ? (missingCount > 0 ? FamilyDataImportStatus.Partial : FamilyDataImportStatus.Succeeded)
            : FamilyDataImportStatus.Failed;

        await _runRepository.UpdateRunAsync(
            runId, status, types.Count, DateTimeOffset.UtcNow, extractionResult.ErrorMessage, ct);

        return new FamilyDataImportResult(
            extractionResult.Success,
            runId,
            types.Count,
            foundCount,
            missingCount,
            extractionResult.ErrorMessage);
    }
}
