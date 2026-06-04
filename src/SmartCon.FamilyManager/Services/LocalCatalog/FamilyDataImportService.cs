using SmartCon.Core.Logging;
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

        // Extract ALL family parameters, not just category-bound ones.
        // Display filtering happens in the UI layer (FamilyPropertiesViewModel).
        var paramNames = Array.Empty<string>();

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

        var existingTypes = await _typeRepository.GetTypesForItemAsync(catalogItemId, ct);
        var existingByName = existingTypes
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var types = new List<FamilyTypeDescriptor>();
        for (var i = 0; i < extractionResult.Types.Count; i++)
        {
            var t = extractionResult.Types[i];
            if (existingByName.TryGetValue(t.TypeName, out var existing))
            {
                var reused = existing with { ExtractionRunId = runId, VersionId = versionId, FileId = fileId };
                types.Add(reused);
            }
            else
            {
                types.Add(new FamilyTypeDescriptor(
                    Guid.NewGuid().ToString(),
                    catalogItemId,
                    t.TypeName,
                    t.SortOrder,
                    versionId,
                    fileId,
                    runId));
            }
        }

        await _typeRepository.SaveTypesForRunAsync(catalogItemId, versionId, fileId, runId, types, ct);

        var resolvedTypes = await _typeRepository.GetTypesForItemAsync(catalogItemId, ct);
        var resolvedByName = resolvedTypes
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var allAttrs = await _attributeDefRepository.GetAllAsync(ct);
        var attrByName = allAttrs.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);

        var values = new List<ExtractedAttributeValue>();
        foreach (var typeData in extractionResult.Types)
        {
            resolvedByName.TryGetValue(typeData.TypeName, out var typeRecord);
            foreach (var val in typeData.Values)
            {
                attrByName.TryGetValue(val.ParameterName, out var attrDef);
                values.Add(new ExtractedAttributeValue(
                    Guid.NewGuid().ToString(),
                    catalogItemId,
                    versionId,
                    fileId,
                    typeRecord?.Id,
                    attrDef?.Id,
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

        if (extractionResult.UntypedValues is not null)
        {
            foreach (var val in extractionResult.UntypedValues)
            {
                attrByName.TryGetValue(val.ParameterName, out var attrDef);
                values.Add(new ExtractedAttributeValue(
                    Guid.NewGuid().ToString(),
                    catalogItemId,
                    versionId,
                    fileId,
                    null,
                    attrDef?.Id,
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

    public async Task MergeMissingValuesAsync(
        string catalogItemId,
        FamilyExtractionResult extractionResult,
        string? versionId,
        string? fileId,
        CancellationToken ct = default)
    {
        var existingTypes = await _typeRepository.GetTypesForItemVersionAsync(catalogItemId, versionId, ct);
        var existingValues = await _valueRepository.GetValuesForItemAsync(catalogItemId, versionId, ct);

        var typeByName = existingTypes.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in existingValues)
        {
            if (v.TypeId is not null)
                existingKeys.Add($"{v.TypeId}|{v.ParameterName}");
        }

        var allAttrs = await _attributeDefRepository.GetAllAsync(ct);
        var attrByName = allAttrs.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);

        var runId = Guid.NewGuid().ToString();
        var now = DateTimeOffset.UtcNow;

        await _runRepository.CreateRunAsync(new FamilyDataImportRun(
            runId, catalogItemId, versionId, fileId, null,
            extractionResult.RevitMajorVersion,
            FamilyDataImportStatus.Succeeded,
            0, now, null, null), ct);

        var typeCatalogParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in existingValues)
            typeCatalogParams.Add(v.ParameterName);

        var matchedTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var valuesToAdd = new List<ExtractedAttributeValue>();
        var skippedExisting = 0;
        var unmatchedSharedParams = new List<FamilyExtractionValueResult>();
        var sharedParams = new List<FamilyExtractionValueResult>();

        foreach (var typeData in extractionResult.Types)
        {
            if (!typeByName.TryGetValue(typeData.TypeName, out var existingType))
            {
                unmatchedSharedParams.AddRange(typeData.Values);
                continue;
            }

            matchedTypeNames.Add(typeData.TypeName);

            foreach (var val in typeData.Values)
            {
                var key = $"{existingType.Id}|{val.ParameterName}";
                if (existingKeys.Contains(key))
                {
                    skippedExisting++;
                    continue;
                }

                attrByName.TryGetValue(val.ParameterName, out var attrDef);
                valuesToAdd.Add(new ExtractedAttributeValue(
                    Guid.NewGuid().ToString(),
                    catalogItemId,
                    versionId,
                    fileId,
                    existingType.Id,
                    attrDef?.Id,
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
                    now));

                if (!typeCatalogParams.Contains(val.ParameterName))
                {
                    sharedParams.Add(val);
                }
            }
        }

        var otherTypes = existingTypes
            .Where(t => !matchedTypeNames.Contains(t.Name))
            .ToList();

        if (otherTypes.Count > 0 && sharedParams.Count > 0)
        {
            SmartConLogger.Info($"[Merge] Propagating {sharedParams.Count} shared params to {otherTypes.Count} unmatched catalog types");
            foreach (var existingType in otherTypes)
            {
                foreach (var sp in sharedParams)
                {
                    var key = $"{existingType.Id}|{sp.ParameterName}";
                    if (existingKeys.Contains(key))
                    {
                        skippedExisting++;
                        continue;
                    }

                    attrByName.TryGetValue(sp.ParameterName, out var attrDef);
                    valuesToAdd.Add(new ExtractedAttributeValue(
                        Guid.NewGuid().ToString(),
                        catalogItemId,
                        versionId,
                        fileId,
                        existingType.Id,
                        attrDef?.Id,
                        null,
                        sp.ParameterName,
                        sp.ParameterScope,
                        sp.StorageType,
                        sp.ValueText,
                        sp.ValueRaw,
                        sp.ValueNumber,
                        sp.UnitTypeId,
                        sp.Status,
                        sp.Message,
                        runId,
                        now));
                }
            }
        }

        if (existingTypes.Count > 0 && unmatchedSharedParams.Count > 0)
        {
            SmartConLogger.Info($"[Merge] Propagating {unmatchedSharedParams.Count} unmatched shared params to {existingTypes.Count} catalog types");
            foreach (var existingType in existingTypes)
            {
                foreach (var val in unmatchedSharedParams)
                {
                    var key = $"{existingType.Id}|{val.ParameterName}";
                    if (existingKeys.Contains(key))
                    {
                        skippedExisting++;
                        continue;
                    }

                    attrByName.TryGetValue(val.ParameterName, out var attrDef);
                    valuesToAdd.Add(new ExtractedAttributeValue(
                        Guid.NewGuid().ToString(),
                        catalogItemId,
                        versionId,
                        fileId,
                        existingType.Id,
                        attrDef?.Id,
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
                        now));
                }
            }
        }

        if (extractionResult.UntypedValues is not null && existingTypes.Count > 0)
        {
            SmartConLogger.Info($"[Merge] Propagating {extractionResult.UntypedValues.Count} untyped values to {existingTypes.Count} catalog types");
            foreach (var existingType in existingTypes)
            {
                foreach (var val in extractionResult.UntypedValues)
                {
                    var key = $"{existingType.Id}|{val.ParameterName}";
                    if (existingKeys.Contains(key))
                    {
                        skippedExisting++;
                        continue;
                    }

                    attrByName.TryGetValue(val.ParameterName, out var attrDef);
                    valuesToAdd.Add(new ExtractedAttributeValue(
                        Guid.NewGuid().ToString(),
                        catalogItemId,
                        versionId,
                        fileId,
                        existingType.Id,
                        attrDef?.Id,
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
                        now));
                }
            }
        }

        SmartConLogger.Info($"[Merge] RESULT: adding={valuesToAdd.Count}, skipped_existing={skippedExisting}, unmatched_shared={unmatchedSharedParams.Count}");
        if (valuesToAdd.Count > 0)
        {
            await _valueRepository.SaveValuesAsync(valuesToAdd, ct);
        }

        await _runRepository.UpdateRunAsync(runId, FamilyDataImportStatus.Succeeded, 0, DateTimeOffset.UtcNow, null, ct);
    }
}
