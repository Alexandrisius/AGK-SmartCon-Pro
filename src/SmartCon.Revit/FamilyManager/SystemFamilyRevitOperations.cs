using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Compatibility;
using SmartCon.Revit.Context;

namespace SmartCon.Revit.FamilyManager;

public sealed class SystemFamilyRevitOperations : ISystemFamilyRevitOperations
{
    private readonly IRevitUIContext _revitUIContext;
    private readonly ITransactionService _transactionService;
    private readonly ILoadableFamilyScanner _loadableFamilyScanner;
    private readonly IMiniProjectMarker _miniProjectMarker;
    private readonly ISystemTypeSyncService _systemTypeSyncService;

    public SystemFamilyRevitOperations(
        IRevitUIContext revitUIContext,
        ITransactionService transactionService,
        ILoadableFamilyScanner loadableFamilyScanner,
        IMiniProjectMarker miniProjectMarker,
        ISystemTypeSyncService systemTypeSyncService)
    {
        _revitUIContext = revitUIContext;
        _transactionService = transactionService;
        _loadableFamilyScanner = loadableFamilyScanner;
        _miniProjectMarker = miniProjectMarker;
        _systemTypeSyncService = systemTypeSyncService;
    }

    public SelectedElementsAnalysis? PickSelectedElements()
    {
        using var _scope = SmartConLogger.BeginScope("SystemRevitOps",
            ("Method", "PickSelectedElements"));
        var uidoc = _revitUIContext.GetUIDocument();
        var doc = uidoc.Document;

        IList<Reference> refs;
        try
        {
            refs = uidoc.Selection.PickObjects(
                ObjectType.Element,
                new Selection.AnyElementSelectionFilter(),
                "Select system family or loadable family elements");
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            // Esc/cancel is a normal user gesture, not an empty import —
            // null lets the caller stay silent instead of showing an error.
            return null;
        }

        var systemTypes = new Dictionary<string, SelectedSystemType>();
        var loadableFamilies = new Dictionary<string, LoadableFamilyInfo>();
        int skippedCount = 0;

        foreach (var r in refs)
        {
            var elem = doc.GetElement(r);
            if (elem is null) continue;

            // #182: log what the picker ACCEPTED (rejections are logged in
            // the filter) — a hosted railing click resolves to the host
            // stairs, and only the accepted-element log tells that story.
            SmartConLogger.Debug(
                $"Picker accepted {elem.GetType().Name} (id={elem.Id.GetValue()}, " +
                $"category='{elem.Category?.Name ?? "(none)"}', name='{elem.Name}')");

            if (elem is FamilyInstance fi)
            {
                var family = fi.Symbol?.Family;
                // #196: non-editable families (curtain wall system panels,
                // «Системная панель») cannot be opened via EditFamily — the
                // loadable import path always fails on them. Same guard as
                // LoadableFamilyScanner (the picker enumerates families on
                // its own, so both paths need the filter).
                if (family is null || family.IsInPlace || !family.IsEditable) { skippedCount++; continue; }
                if (!loadableFamilies.ContainsKey(family.UniqueId))
                {
                    loadableFamilies[family.UniqueId] = new LoadableFamilyInfo(
                        FamilyName: family.Name,
                        FamilyUniqueId: family.UniqueId,
                        CategoryName: family.FamilyCategory?.Name ?? "Unknown",
                        TypeCount: family.GetFamilySymbolIds().Count);
                }
                continue;
            }

            var typeId = elem.GetTypeId();
            if (typeId == ElementId.InvalidElementId)
            {
                // #182: log WHY a picked element is skipped (was silent).
                skippedCount++;
                SmartConLogger.Debug(
                    $"Picker skipped {elem.GetType().Name} (id={elem.Id.GetValue()}, name='{elem.Name}') — GetTypeId is invalid");
                continue;
            }

            // #181 (scope corrected 2026-08-06): the insulation-host filter is
            // NOT applied in the picker — an explicit user pick IS the import
            // intent. The filter exists only for "Импорт активного файла" in
            // SmartCon mini-projects (see AnalyzeActiveProject).

            var typeElem = doc.GetElement(typeId);
            if (typeElem is null)
            {
                skippedCount++;
                SmartConLogger.Debug(
                    $"Picker skipped {elem.GetType().Name} (id={elem.Id.GetValue()}) — type element {typeId.GetValue()} not found");
                continue;
            }

            var category = typeElem.Category;
            var categoryName = category?.Name ?? "Unknown";
            var builtInCategory = CategoryCompat.GetBuiltInCategory(category);

            if (builtInCategory == BuiltInCategory.INVALID)
            {
                SmartConLogger.Debug(
                    $"Type '{typeElem.Name}' has no resolvable BuiltInCategory " +
                    $"(category='{categoryName}') — will copy as type-only, no instances");
            }
            else if (!SystemCategoryRegistry.SupportedCategories.Contains(builtInCategory))
            {
                SmartConLogger.Warn(
                    $"Type '{typeElem.Name}' has BuiltInCategory='{builtInCategory}' " +
                    $"(category='{categoryName}') which is not in the supported set — " +
                    $"will copy as type-only, no instances");
                builtInCategory = BuiltInCategory.INVALID;
            }

            if (!systemTypes.ContainsKey(typeElem.UniqueId))
            {
                systemTypes[typeElem.UniqueId] = new SelectedSystemType(
                    typeElem.UniqueId,
                    typeElem.Name,
                    categoryName,
                    builtInCategory,
                    (typeElem as ElementType)?.FamilyName,
                    (typeElem as ElementType) is { } et ? SystemFamilyKeyResolver.Resolve(et) : null);
            }
        }

        if (skippedCount > 0)
            SmartConLogger.Info(
                $"Skipped {skippedCount} element(s) without resolvable type/category");

        return new SelectedElementsAnalysis(
            SystemTypes: systemTypes.Values.ToList(),
            LoadableFamilies: loadableFamilies.Values.ToList());
    }

    public IReadOnlyList<CategoryAnalysis> AnalyzeActiveProject(Document activeDoc)
    {
        using var _scope = SmartConLogger.BeginScope("SystemRevitOps",
            ("Method", "AnalyzeActiveProject"));
        if (activeDoc is null) return [];

        // #181 (scope corrected 2026-08-06): pipes/ducts existing ONLY as
        // insulation hosts (the insulation type cannot exist without a host,
        // CF-4720) are artifacts of the Revit API. The exclusion applies ONLY
        // to SmartCon mini-projects (ES marker, ADR-062): a mini-project
        // carries host pipes/ducts solely to stage insulation types, and those
        // hosts are staging workarounds, not import candidates. In a REGULAR
        // working project an insulated pipe/duct is real user content and is
        // offered for import like anything else (owner decision: «в рабочем
        // проекте я ожидаю и изоляцию, и трубу в batch-диалоге»).
        var excludeInsulationHosts = _miniProjectMarker.IsMiniProject(activeDoc);
        if (excludeInsulationHosts)
        {
            SmartConLogger.Debug("Insulation-host exclusion active (SmartCon mini-project)");
        }
        HashSet<ElementId>? insulatedHostIds = null;

        var result = new List<CategoryAnalysis>();

        foreach (var entry in SystemCategoryRegistry.Entries)
        {
            try
            {
                var instances = new FilteredElementCollector(activeDoc)
                    .OfCategory(entry.Category)
                    .WhereElementIsNotElementType()
                    .AsEnumerable();

                if (excludeInsulationHosts && IsInsulationHostCategory(entry.Category))
                {
                    insulatedHostIds ??= CollectInsulatedHostIds(activeDoc);
                    instances = instances.Where(e => !insulatedHostIds.Contains(e.Id));
                }

                var placedTypeIds = new HashSet<ElementId>(
                    instances
                        .Select(e => e.GetTypeId())
                        .Where(id => id != null && id != ElementId.InvalidElementId));

                if (placedTypeIds.Count == 0) continue;

                var types = new List<SystemTypeInfo>();
                string? documentCategoryName = null;
                foreach (var typeId in placedTypeIds)
                {
                    var typeElem = activeDoc.GetElement(typeId);
                    if (typeElem is null) continue;
                    // The category display name MUST come from the document
                    // (same source as the picker and the snapshot extractor) —
                    // the registry name is only a fallback. Display names of
                    // built-in categories differ per template/version/locale
                    // and the document is the single source of truth.
                    documentCategoryName ??= typeElem.Category?.Name;
                    // #183: FamilyName flows into family_types.family_name —
                    // the sync identity is (family, name), never name alone.
                    // #190 (ADR-064): FamilyKey is the locale-invariant
                    // identity — persisted into family_types.family_key (V27).
                    types.Add(new SystemTypeInfo(
                        typeElem.Name,
                        typeElem.UniqueId,
                        (typeElem as ElementType)?.FamilyName,
                        (typeElem as ElementType) is { } et ? SystemFamilyKeyResolver.Resolve(et) : null));
                }

                if (types.Count == 0) continue;

                result.Add(new CategoryAnalysis(entry.Category, documentCategoryName ?? entry.DisplayName, types));
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"{entry.DisplayName}: {ex.Message} [Action: категория пропущена в анализе; пришлите smartcon.log разработчику, если категория должна импортироваться]");
            }
        }

        return result;
    }

    public CreateCleanProjectResult CreateCleanProjectWithTypesAndInstances(
        Document sourceDoc,
        IReadOnlyList<string> typeUniqueIds,
        BuiltInCategory category,
        string displayName,
        string managedRvtPath,
        string? catalogItemId = null)
    {
        if (sourceDoc is null)
            return new CreateCleanProjectResult(false, null, "sourceDoc is null", 0);
        if (typeUniqueIds is null || typeUniqueIds.Count == 0)
            return new CreateCleanProjectResult(false, null, "No typeUniqueIds provided", 0);
        if (string.IsNullOrEmpty(managedRvtPath))
            return new CreateCleanProjectResult(false, null, "managedRvtPath is empty", 0);

        var sourceTypeIds = new List<ElementId>();
        foreach (var uid in typeUniqueIds)
        {
            var elem = sourceDoc.GetElement(uid);
            if (elem is not null) sourceTypeIds.Add(elem.Id);
        }
        if (sourceTypeIds.Count == 0)
            return new CreateCleanProjectResult(false, null, "No type elements found in source", 0);

        var app = sourceDoc.Application;
        Document? newDoc = null;
        try
        {
            newDoc = app.NewProjectDocument(UnitSystem.Metric);
        }
        catch (Exception ex)
        {
            return new CreateCleanProjectResult(false, null, $"Failed to create project: {ex.Message}", 0);
        }

        try
        {
            // #104: the default template may already contain types of this
            // category with the same names (e.g. wall "Стена 1"). CopyElements
            // does NOT skip the incoming type on collision — it copies it with
            // an auto-generated name ("Стена 2") even with
            // UseDestinationTypes, and the mini-project then stores the type
            // under a foreign name. Template types cannot be deleted upfront
            // (Revit forbids deleting the last type of a system family), so
            // they are renamed away before the copy and deleted after it.
            var sourceTypeNames = new List<string>();
            foreach (var id in sourceTypeIds)
            {
                var name = sourceDoc.GetElement(id)?.Name;
                if (!string.IsNullOrEmpty(name)) sourceTypeNames.Add(name!);
            }
            TemplateCollisionResolver.RenameConflictingTemplateTypes(
                _transactionService, newDoc, category, sourceTypeNames);

            ICollection<ElementId> copiedTypeIds;
            // ADR-072 (#254): MEPCurve categories (pipe/duct/flex/conduit/
            // cable tray) are staged MANUALLY — Duplicate a same-family
            // template prototype + parameter writes + segment sync + slim
            // routing. Nothing is copied, so no fitting family and no
            // family-internal material record enters the mini-project and
            // the material-duplication class of #254 cannot be born
            // (Revit 2024+ duplicates colliding material names inside
            // CopyElements; there is no API hook to prevent it). Non-MEP
            // categories keep the CopyElements path (no routing deps drag
            // along; evaluated separately in ADR-072 Phase 4).
            var isMepCurveCategory = sourceTypeIds.Any(id => sourceDoc.GetElement(id) is MEPCurveType);
            if (isMepCurveCategory)
            {
                copiedTypeIds = StageMepCurveTypesManually(
                    sourceDoc, newDoc, sourceTypeIds, category, displayName);
            }
            else
            {
                ICollection<ElementId> copied = [];
                var copyCommitted = _transactionService.RunInTransaction(newDoc, "Copy system types", doc =>
                {
                    var options = new CopyPasteOptions();
                    options.SetDuplicateTypeNamesHandler(new SkipDuplicateTypesHandler());

                    copied = ElementTransformUtils.CopyElements(
                        sourceDoc, sourceTypeIds, doc, null, options);
                });
                copiedTypeIds = copied;

                if (!copyCommitted)
                {
                    // Silent rollback (#178 pattern): without this guard the
                    // mini-project would be SAVED EMPTY and reported as success.
                    SmartConLogger.Warn(
                        $"'{displayName}': 'Copy system types' rolled back or copied 0 types — mini-project NOT saved. " +
                        "[Action: check the source category has placeable types and re-run the import]");
                    return new CreateCleanProjectResult(
                        false, null, "Copy system types rolled back or copied 0 types", 0);
                }
            }

            if (copiedTypeIds.Count == 0)
            {
                SmartConLogger.Warn(
                    $"'{displayName}': staging produced 0 types — mini-project NOT saved. " +
                    "[Action: check the source category has placeable types and re-run the import]");
                return new CreateCleanProjectResult(
                    false, null, "Staging produced 0 types", 0);
            }

            // The renamed template types are intentionally LEFT in the
            // mini-project: deleting them is unreliable (Revit forbids
            // deleting the last type of a system family and, in a real UI
            // session, shows a modal error dialog that freezes the batch
            // import). They are harmless — catalog type lists are built from
            // placed instances and the synchronizer matches types by name.

            // Размещение инстансов в новом проекте. Общая внешняя транзакция
            // убрана (ADR-027 Phase 2): каждый handler управляет транзакциями
            // сам — StairsEditScope запрещает старт внутри активной транзакции.
            var placedInstancesByType = PlaceInstancesOnGrid(newDoc, copiedTypeIds, category);

            // Нормализация диаметров/размеров на размещённых инстансах.
            // Причина: новый проект (NewProjectDocument Metric) создаёт типы
            // с дефолтными размерами (≈ 152 мм / 6"), а не из источника.
            // Транзакция на каждый тип (как просил пользователь).
            NormalizeInstanceDimensions(newDoc, placedInstancesByType, category);

            var placedCount = placedInstancesByType.Sum(kv => kv.Value.Count);

            // #188: mark the staged file as a SmartCon reference mini-project
            // BEFORE SaveAs — the document is closed without saving afterwards,
            // so the marker must already be inside the saved file. The marker
            // authorizes the safe close-without-save after reimport (#186) and
            // excludes the file from active-DB auto-switching.
            _miniProjectMarker.MarkAsMiniProject(newDoc, catalogItemId);

            // v2.0.0: SaveAs directly into managed storage, no temp staging.
            var managedDir = Path.GetDirectoryName(managedRvtPath);
            if (!string.IsNullOrEmpty(managedDir) && !Directory.Exists(managedDir))
            {
                Directory.CreateDirectory(managedDir);
            }
            if (File.Exists(managedRvtPath))
            {
                File.SetAttributes(managedRvtPath, File.GetAttributes(managedRvtPath) & ~FileAttributes.ReadOnly);
                File.Delete(managedRvtPath);
            }
            newDoc.SaveAs(managedRvtPath, new SaveAsOptions { OverwriteExistingFile = true });
            File.SetAttributes(managedRvtPath, File.GetAttributes(managedRvtPath) | FileAttributes.ReadOnly);

            SmartConLogger.Info(
                $"'{displayName}': copied={copiedTypeIds.Count}, placed={placedCount}");

            return new CreateCleanProjectResult(
                true, managedRvtPath, null, copiedTypeIds.Count, displayName, placedCount);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"Failed: {ex.GetType().Name}: {ex.Message}");
            return new CreateCleanProjectResult(false, null, ex.Message, 0);
        }
        finally
        {
            if (newDoc is not null)
            {
                try { newDoc.Close(false); } catch { }
                try { Marshal.ReleaseComObject(newDoc); } catch { }
            }
        }
    }

    /// <summary>
    /// ADR-072 (#254): manual staging of MEPCurve types — per type
    /// <see cref="ISystemTypeSyncService.StageTypeFromSource"/> (Duplicate
    /// a same-family template prototype + parameter writes + segment sync +
    /// slim routing). Types whose system family has NO prototype in the
    /// template (<see cref="SystemTypeSyncStatus.FamilyNotFound"/>) fall
    /// back to CopyElements (ADR-072 §2.7 п.6 — prototype-less families;
    /// for MEPCurve this is exotic — P0.3 verified every MEPCurve family
    /// has a template prototype, but the guard is mandatory, never fatal).
    /// Returns the ids of the staged types in <paramref name="newDoc"/>.
    /// </summary>
    private List<ElementId> StageMepCurveTypesManually(
        Document sourceDoc,
        Document newDoc,
        IReadOnlyList<ElementId> sourceTypeIds,
        BuiltInCategory category,
        string displayName)
    {
        var stagedNames = new List<string>();
        var fallbackIds = new List<ElementId>();

        foreach (var id in sourceTypeIds)
        {
            if (sourceDoc.GetElement(id) is not ElementType sourceType)
                continue;

            var result = _systemTypeSyncService.StageTypeFromSource(
                sourceDoc, newDoc, sourceType.Name, (int)category,
                sourceType.FamilyName, SystemFamilyKeyResolver.Resolve(sourceType));

            if (result.IsSuccess)
            {
                stagedNames.Add(sourceType.Name);
                continue;
            }

            SmartConLogger.Warn(
                $"'{displayName}': manual staging of '{sourceType.Name}' failed ({result.Status}: {result.ErrorMessage}) — " +
                "the type falls back to CopyElements. " +
                "[Action: проверьте лог; тип будет скопирован, а не создан вручную]");
            fallbackIds.Add(id);
        }

        var stagedIds = new List<ElementId>();
        if (stagedNames.Count > 0)
        {
            var nameSet = new HashSet<string>(stagedNames, StringComparer.Ordinal);
            foreach (var t in new FilteredElementCollector(newDoc)
                .OfClass(typeof(ElementType))
                .OfCategory(category)
                .Cast<ElementType>())
            {
                if (nameSet.Contains(t.Name))
                    stagedIds.Add(t.Id);
            }
        }

        if (fallbackIds.Count > 0)
        {
            ICollection<ElementId> copied = [];
            var committed = _transactionService.RunInTransaction(newDoc, "Copy system types (fallback)", doc =>
            {
                var options = new CopyPasteOptions();
                options.SetDuplicateTypeNamesHandler(new SkipDuplicateTypesHandler());
                copied = ElementTransformUtils.CopyElements(sourceDoc, fallbackIds, doc, null, options);
            });
            if (committed)
            {
                stagedIds.AddRange(copied);
            }
            else
            {
                SmartConLogger.Warn(
                    $"'{displayName}': CopyElements fallback rolled back for {fallbackIds.Count} type(s). " +
                    "[Action: типы пропущены; проверьте лог транзакции и повторите импорт]");
            }
        }

        SmartConLogger.Info(
            $"'{displayName}': manual staging — staged={stagedNames.Count}, copy-fallback={fallbackIds.Count}");
        return stagedIds;
    }

    /// <summary>
    /// Размещает инстансы скопированных типов на сетке 2×2 м на Level 1.
    /// Handler категории сам управляет транзакциями (ADR-027 Phase 2 —
    /// StairsEditScope нельзя стартовать внутри активной транзакции).
    /// Версионная доступность (потолки R22+, ограждения R25+) фильтруется
    /// <see cref="SystemCategoryPlacementAvailability"/>; недоступная на этой
    /// версии категория до staging не доходит — её отсекает импортный гейт.
    /// </summary>
    /// <returns>Словарь: typeId → список ID размещённых инстансов этого типа.</returns>
    private Dictionary<ElementId, List<ElementId>> PlaceInstancesOnGrid(
        Document newDoc, ICollection<ElementId> typeIds, BuiltInCategory category)
    {
        var instancesByType = new Dictionary<ElementId, List<ElementId>>();

        var revitMajor = int.TryParse(newDoc.Application.VersionNumber, out var v) ? v : 0;
        var handler = SystemCategoryRegistry.GetPlacementHandler(category, revitMajor);
        if (handler is null)
        {
            SmartConLogger.Info($"No placement handler for {category} on Revit {revitMajor}; types copied only");
            return instancesByType;
        }

        var level = new FilteredElementCollector(newDoc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(l => l.Elevation)
            .FirstOrDefault();
        if (level is null)
        {
            SmartConLogger.Warn("No Level 1 in new project");
            return instancesByType;
        }

        var i = 0;
        foreach (var typeId in typeIds)
        {
            var type = newDoc.GetElement(typeId);
            if (type is null) continue;

            // 2m grid spacing, 1m instance length, origin (0,0,0)
            var gridSizeFt = RevitUnitsCompat.MetersToInternal(2.0);
            var lengthFt = RevitUnitsCompat.MetersToInternal(1.0);
            var x = (i % 10) * gridSizeFt;
            var y = (i / 10) * gridSizeFt;
            var z = level.Elevation;

            var start = new XYZ(x, y, z);
            var end = new XYZ(x + lengthFt, y, z);

            try
            {
                var created = handler(newDoc, _transactionService, type, level, start, end);
                if (created is not null)
                {
                    if (!instancesByType.TryGetValue(typeId, out var list))
                    {
                        list = new List<ElementId>();
                        instancesByType[typeId] = list;
                    }
                    list.Add(created.Id);
                }
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn($"Failed type '{type.Name}': {ex.Message}");
            }

            i++;
        }

        return instancesByType;
    }

    /// <summary>
    /// Нормализует размеры размещённых инстансов до круглых значений.
    /// Причина: в новом проекте pipe type получает дефолтный диаметр проекта
    /// (≈ 152 мм для Imperial-шаблона Revit), а не из источника. Без явной
    /// установки через транзакцию на каждый тип пользователь видит "кривые"
    /// дробные диаметры.
    /// Использует BuiltInParameter (locale-independent), а не LookupParameter("Diameter"),
    /// т.к. имя параметра в UI зависит от языка проекта ("Diameter" / "Диаметр").
    /// </summary>
    private void NormalizeInstanceDimensions(
        Document newDoc,
        Dictionary<ElementId, List<ElementId>> instancesByType,
        BuiltInCategory category)
    {
        if (instancesByType.Count == 0) return;

        BuiltInParameter? diamBip = null;
        BuiltInParameter? widthBip = null;
        BuiltInParameter? heightBip = null;
        double diameterFt = 0, widthFt = 0, heightFt = 0;

        switch (category)
        {
            case BuiltInCategory.OST_PipeCurves:
            case BuiltInCategory.OST_FlexPipeCurves:
                // FlexPipe inherits MEPCurve, so it exposes the same
                // RBS_PIPE_DIAMETER_PARAM built-in parameter as Pipe.
                // https://www.revitapidocs.com/2027/22b56931-ade4-178f-e118-7a0e436a2fbb.htm
                diamBip = BuiltInParameter.RBS_PIPE_DIAMETER_PARAM;
                diameterFt = RevitUnitsCompat.MetersToInternal(0.1);
                break;
            case BuiltInCategory.OST_DuctCurves:
            case BuiltInCategory.OST_FlexDuctCurves:
                diamBip = BuiltInParameter.RBS_CURVE_DIAMETER_PARAM;
                widthBip = BuiltInParameter.RBS_CURVE_WIDTH_PARAM;
                heightBip = BuiltInParameter.RBS_CURVE_HEIGHT_PARAM;
                diameterFt = RevitUnitsCompat.MetersToInternal(0.2);
                widthFt = RevitUnitsCompat.MetersToInternal(0.2);
                heightFt = RevitUnitsCompat.MetersToInternal(0.2);
                break;
            case BuiltInCategory.OST_Conduit:
                diamBip = BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM;
                diameterFt = RevitUnitsCompat.MetersToInternal(0.05);
                break;
            case BuiltInCategory.OST_CableTray:
                widthBip = BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM;
                heightBip = BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM;
                widthFt = RevitUnitsCompat.MetersToInternal(0.1);
                heightFt = RevitUnitsCompat.MetersToInternal(0.05);
                break;
            default:
                return;
        }

        foreach (var kvp in instancesByType)
        {
            var typeId = kvp.Key;
            var instanceIds = kvp.Value;
            var typeName = newDoc.GetElement(typeId)?.Name ?? typeId.ToString();
            try
            {
                _transactionService.RunInTransaction(newDoc, $"Normalize dimensions: {typeName}", doc =>
                {
                    foreach (var instId in instanceIds)
                    {
                        var inst = doc.GetElement(instId);
                        if (inst is null) continue;

                        if (diamBip.HasValue && diameterFt > 0)
                            inst.get_Parameter(diamBip.Value)?.Set(diameterFt);
                        if (widthBip.HasValue && widthFt > 0)
                            inst.get_Parameter(widthBip.Value)?.Set(widthFt);
                        if (heightBip.HasValue && heightFt > 0)
                            inst.get_Parameter(heightBip.Value)?.Set(heightFt);
                    }
                });
                SmartConLogger.Info(
                    $"'{typeName}': applied to {instanceIds.Count} instance(s)");
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn($"'{typeName}': {ex.Message} [Action: проверьте, что тип семейства поддерживает изменение диаметра/ширины/высоты через стандартные параметры]");
            }
        }
    }

    private static string SanitizeFileName(string name)
    {
        // v2.0.0: the actual sanitisation rule lives in
        // SmartCon.Core.Services.FamilyManager.SafeFileName.SanitizeFileName
        // — this method is a thin redirect so the duplicate is gone and
        // the rule is defined in exactly one place. The next refactor
        // pass should inline the call sites and delete this wrapper.
        return SafeFileName.SanitizeFileName(name);
    }

    /// <summary>#181: делегаты в <see cref="InsulationHostFilter"/> (общий фильтр, используется и детектом категории staged-файла).</summary>
    private static bool IsInsulationHostCategory(BuiltInCategory bic)
        => InsulationHostFilter.IsInsulationHostCategory(bic);

    private static HashSet<ElementId> CollectInsulatedHostIds(Document doc)
        => InsulationHostFilter.CollectInsulatedHostIds(doc);

    private sealed class SkipDuplicateTypesHandler : IDuplicateTypeNamesHandler
    {
        public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)
        {
            return DuplicateTypeAction.UseDestinationTypes;
        }
    }
}
