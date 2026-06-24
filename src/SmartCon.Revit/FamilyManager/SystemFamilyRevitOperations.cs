using System.Reflection;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
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

    public SystemFamilyRevitOperations(
        IRevitUIContext revitUIContext,
        ITransactionService transactionService,
        ILoadableFamilyScanner loadableFamilyScanner)
    {
        _revitUIContext = revitUIContext;
        _transactionService = transactionService;
        _loadableFamilyScanner = loadableFamilyScanner;
    }

    public SelectedElementsAnalysis PickSelectedElements()
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
            return new SelectedElementsAnalysis([], []);
        }

        var systemTypes = new Dictionary<string, SelectedSystemType>();
        var loadableFamilies = new Dictionary<string, LoadableFamilyInfo>();
        int skippedCount = 0;

        foreach (var r in refs)
        {
            var elem = doc.GetElement(r);
            if (elem is null) continue;

            if (elem is FamilyInstance fi)
            {
                var family = fi.Symbol?.Family;
                if (family is null || family.IsInPlace) { skippedCount++; continue; }
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
            if (typeId == ElementId.InvalidElementId) { skippedCount++; continue; }

            var typeElem = doc.GetElement(typeId);
            if (typeElem is null) { skippedCount++; continue; }

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
                    builtInCategory);
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

        var result = new List<CategoryAnalysis>();

        foreach (var entry in SystemCategoryRegistry.Entries)
        {
            try
            {
                var placedTypeIds = new HashSet<ElementId>(
                    new FilteredElementCollector(activeDoc)
                        .OfCategory(entry.Category)
                        .WhereElementIsNotElementType()
                        .Select(e => e.GetTypeId())
                        .Where(id => id != null && id != ElementId.InvalidElementId));

                if (placedTypeIds.Count == 0) continue;

                var types = new List<SystemTypeInfo>();
                foreach (var typeId in placedTypeIds)
                {
                    var typeElem = activeDoc.GetElement(typeId);
                    if (typeElem is null) continue;
                    types.Add(new SystemTypeInfo(typeElem.Name, typeElem.UniqueId));
                }

                if (types.Count == 0) continue;

                result.Add(new CategoryAnalysis(entry.Category, entry.DisplayName, types));
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn($"{entry.DisplayName}: {ex.Message}");
            }
        }

        return result;
    }

    public CreateCleanProjectResult CreateCleanProjectWithTypesAndInstances(
        Document sourceDoc,
        IReadOnlyList<string> typeUniqueIds,
        BuiltInCategory category,
        string displayName,
        string managedRvtPath)
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
            ICollection<ElementId> copiedTypeIds = [];
            _transactionService.RunInTransaction(newDoc, "Copy system types", doc =>
            {
                var options = new CopyPasteOptions();
                options.SetDuplicateTypeNamesHandler(new SkipDuplicateTypesHandler());

                copiedTypeIds = ElementTransformUtils.CopyElements(
                    sourceDoc, sourceTypeIds, doc, null, options);
            });

            // Размещение инстансов в новом проекте (только для линейных категорий).
            Dictionary<ElementId, List<ElementId>> placedInstancesByType = [];
            _transactionService.RunInTransaction(newDoc, "Place instances on grid", doc =>
            {
                placedInstancesByType = PlaceInstancesOnGrid(doc, copiedTypeIds, category);
            });

            // Нормализация диаметров/размеров на размещённых инстансах.
            // Причина: новый проект (NewProjectDocument Metric) создаёт типы
            // с дефолтными размерами (≈ 152 мм / 6"), а не из источника.
            // Транзакция на каждый тип (как просил пользователь).
            NormalizeInstanceDimensions(newDoc, placedInstancesByType, category);

            var placedCount = placedInstancesByType.Sum(kv => kv.Value.Count);

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
            newDoc.Close(false);
            try { Marshal.ReleaseComObject(newDoc); } catch { }
            newDoc = null;

            SmartConLogger.Info(
                $"'{displayName}': copied={copiedTypeIds.Count}, placed={placedCount}");

            return new CreateCleanProjectResult(
                true, managedRvtPath, null, copiedTypeIds.Count, displayName, placedCount);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"Failed: {ex.GetType().Name}: {ex.Message}");
            if (newDoc is not null)
            {
                try { newDoc.Close(false); } catch { }
                try { Marshal.ReleaseComObject(newDoc); } catch { }
            }
            return new CreateCleanProjectResult(false, null, ex.Message, 0);
        }
    }

    /// <summary>
    /// Размещает инстансы скопированных типов на сетке 2×2 м на Level 1.
    /// Поддерживаются только ЛИНЕЙНЫЕ категории (двухточечное размещение).
    /// Категории с другой геометрией (Floors, Roofs, Ceilings — CurveLoop; Stairs/Railings — сложные) — копируются только как типы.
    /// </summary>
    /// <returns>Словарь: typeId → список ID размещённых инстансов этого типа.</returns>
    private static Dictionary<ElementId, List<ElementId>> PlaceInstancesOnGrid(
        Document newDoc, ICollection<ElementId> typeIds, BuiltInCategory category)
    {
        var instancesByType = new Dictionary<ElementId, List<ElementId>>();

        var handler = SystemCategoryRegistry.GetPlacementHandler(category);
        if (handler is null)
        {
            SmartConLogger.Info($"No placement handler for {category}; types copied only");
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
                var created = handler(newDoc, type, level, start, end);
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
    private static void NormalizeInstanceDimensions(
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
            using (var tx = new Transaction(newDoc, $"Normalize dimensions: {typeName}"))
            {
                try
                {
                    tx.Start();
                    foreach (var instId in instanceIds)
                    {
                        var inst = newDoc.GetElement(instId);
                        if (inst is null) continue;

                        if (diamBip.HasValue && diameterFt > 0)
                            inst.get_Parameter(diamBip.Value)?.Set(diameterFt);
                        if (widthBip.HasValue && widthFt > 0)
                            inst.get_Parameter(widthBip.Value)?.Set(widthFt);
                        if (heightBip.HasValue && heightFt > 0)
                            inst.get_Parameter(heightBip.Value)?.Set(heightFt);
                    }
                    tx.Commit();
                    SmartConLogger.Info(
                        $"'{typeName}': applied to {instanceIds.Count} instance(s)");
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn($"'{typeName}': {ex.Message}");
                }
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

    private sealed class SkipDuplicateTypesHandler : IDuplicateTypeNamesHandler
    {
        public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)
        {
            return DuplicateTypeAction.UseDestinationTypes;
        }
    }
}

/// <summary>
/// Реестр поддерживаемых системных категорий и их handlers для placement.
/// Содержит ОДНО место для добавления/удаления категорий.
///
/// РЕАЛИЗОВАННЫЕ КАТЕГОРИИ (Phase 1):
///   - OST_PipeCurves                       -> Pipe.Create (2 точки)
///   - OST_FlexPipeCurves                   -> FlexPipe.Create (2 точки + tangents)
///   - OST_DuctCurves                       -> Duct.Create (2 точки)
///   - OST_FlexDuctCurves                   -> FlexDuct.Create (2 точки + tangents)
///   - OST_Walls                            -> Wall.Create (Line)
///   - OST_Conduit                          -> Conduit.Create (2 точки)
///   - OST_CableTray                        -> CableTray.Create (2 точки)
///
/// НЕРЕАЛИЗОВАННЫЕ КАТЕГОРИИ (Phase 2 TODO — нужны CurveLoop или сложная логика):
///   - OST_Floors, OST_Roofs, OST_Ceilings  -> NewFloor/NewRoof/NewCeiling (CurveLoop)
///   - OST_Stairs, OST_Railings             -> Stairs.Create, Railing.Create (сложно)
///   - OST_PipeInsulations, OST_DuctInsulations -> InsulationLiningBase (требует host)
/// Типы этих категорий КОПИРУЮТСЯ, но НЕ размещаются.
/// </summary>
internal static class SystemCategoryRegistry
{
    public sealed record Entry(
        BuiltInCategory Category,
        string DisplayName,
        Func<Document, Element, Level, XYZ, XYZ, Element?>? PlacementHandler);

    public static readonly IReadOnlyList<Entry> Entries = BuildEntries();

    public static Func<Document, Element, Level, XYZ, XYZ, Element?>? GetPlacementHandler(BuiltInCategory category)
    {
        foreach (var e in Entries)
        {
            if (e.Category == category) return e.PlacementHandler;
        }
        return null;
    }

    /// <summary>
    /// Единый источник правды для набора поддерживаемых системных категорий.
    /// Используется:
    ///   - <see cref="Selection.AnyElementSelectionFilter"/> для фильтрации выбора в Revit UI
    ///   - <see cref="PickSelectedElements"/> для проверки
    ///     соответствия категории типа каноническому списку (defense in depth)
    /// </summary>
    public static readonly HashSet<BuiltInCategory> SupportedCategories =
        new(Entries.Select(e => e.Category));

    private static IReadOnlyList<Entry> BuildEntries()
    {
        return new List<Entry>
        {
            new(BuiltInCategory.OST_PipeCurves,      "Трубы",        PlacePipe),
            new(BuiltInCategory.OST_FlexPipeCurves,  "Гибкие трубы", PlaceFlexPipe),
            new(BuiltInCategory.OST_DuctCurves,      "Воздуховоды",  PlaceDuct),
            new(BuiltInCategory.OST_FlexDuctCurves,  "Гибкие воздуховоды", PlaceFlexDuct),
            new(BuiltInCategory.OST_Conduit,         "Короба",       PlaceConduit),
            new(BuiltInCategory.OST_CableTray,       "Лотки",        PlaceCableTray),
            new(BuiltInCategory.OST_Walls,           "Стены",        PlaceWall),

            // Копируются, но НЕ размещаются (Phase 2 TODO — см. ADR-027 §"Phase 2 TODO"):
            //
            // Эти категории зарегистрированы с PlacementHandler = null, чтобы
            // AnalyzeActiveProject / PickSelectedElements продолжали показывать
            // их в batch dialog. Тип копируется в mini-rvt (copied=N), но
            // инстанс НЕ размещается (placed=0), и SystemFamilyAttributeExtractor
            // пишет 0 атрибутов. User decision 2026-06-08: оставить видимыми
            // для awareness, реализацию placement делать в Phase 2.
            //
            // Blockers (детали в ADR-027):
            //   Floors / Roofs / Ceilings — требуют CurveLoop, не 2-точечный line
            //   Stairs                  — многоуровневая иерархия
            //   Railings                — требует host + continuous path
            //   PipeInsulations         — требует host pipe в destination doc
            //   DuctInsulations         — требует host duct в destination doc
            //
            // При реализации Phase 2: сигнатура PlacementHandler изменится
            // (Floor.NewFloor нужен CurveLoop, InsulationLiningBase.Create нужен host).
            new(BuiltInCategory.OST_Floors,          "Перекрытия",   null),
            new(BuiltInCategory.OST_Roofs,           "Крыши",        null),
            new(BuiltInCategory.OST_Ceilings,        "Потолки",      null),
            new(BuiltInCategory.OST_Stairs,          "Лестницы",     null),
            new(BuiltInCategory.OST_Railings,        "Ограждения",   null),
            new(BuiltInCategory.OST_PipeInsulations, "Изоляция труб", null),
            new(BuiltInCategory.OST_DuctInsulations, "Изоляция воздуховодов", null),
        };
    }

    private static Element? PlacePipe(Document doc, Element type, Level level, XYZ start, XYZ end)
    {
        if (type is not PipeType pipeType) return null;
        var sysType = new FilteredElementCollector(doc)
            .OfClass(typeof(PipingSystemType))
            .Cast<PipingSystemType>()
            .FirstOrDefault();
        if (sysType is null) return null;
        return Pipe.Create(doc, sysType.Id, pipeType.Id, level.Id, start, end);
    }

    /// <summary>
    /// Places a flex pipe instance on the grid. <see cref="FlexPipe.Create"/>
    /// requires a <see cref="FlexPipeType"/> (not a regular <see cref="PipeType"/>)
    /// and an array of intermediate points, so the regular <see cref="PlacePipe"/>
    /// handler cannot be reused. The number of points and the tangent vectors
    /// are minimal — Revit derives the spline from the points alone.
    /// </summary>
    private static Element? PlaceFlexPipe(Document doc, Element type, Level level, XYZ start, XYZ end)
    {
        if (type is not FlexPipeType flexPipeType) return null;
        var sysType = new FilteredElementCollector(doc)
            .OfClass(typeof(PipingSystemType))
            .Cast<PipingSystemType>()
            .FirstOrDefault();
        if (sysType is null) return null;

        var points = new List<XYZ> { start, end };
        var tangent = XYZ.BasisX;
        return FlexPipe.Create(doc, sysType.Id, flexPipeType.Id, level.Id, tangent, tangent, points);
    }

    private static Element? PlaceDuct(Document doc, Element type, Level level, XYZ start, XYZ end)
    {
        if (type is not DuctType ductType) return null;
        var sysType = new FilteredElementCollector(doc)
            .OfClass(typeof(MechanicalSystemType))
            .Cast<MechanicalSystemType>()
            .FirstOrDefault();
        if (sysType is null) return null;
        return Duct.Create(doc, sysType.Id, ductType.Id, level.Id, start, end);
    }

    /// <summary>
    /// Places a flex duct instance on the grid. <see cref="FlexDuct.Create"/>
    /// requires a <see cref="FlexDuctType"/> (not a regular <see cref="DuctType"/>)
    /// and an array of intermediate points, so the regular <see cref="PlaceDuct"/>
    /// handler cannot be reused. Mirrors <see cref="PlaceFlexPipe"/>.
    /// </summary>
    private static Element? PlaceFlexDuct(Document doc, Element type, Level level, XYZ start, XYZ end)
    {
        if (type is not FlexDuctType flexDuctType) return null;
        var sysType = new FilteredElementCollector(doc)
            .OfClass(typeof(MechanicalSystemType))
            .Cast<MechanicalSystemType>()
            .FirstOrDefault();
        if (sysType is null) return null;

        var points = new List<XYZ> { start, end };
        var tangent = XYZ.BasisX;
        return FlexDuct.Create(doc, sysType.Id, flexDuctType.Id, level.Id, tangent, tangent, points);
    }

    private static Element? PlaceConduit(Document doc, Element type, Level level, XYZ start, XYZ end)
    {
        var assembly = doc.GetType().Assembly;
        var conduitType = assembly.GetType("Autodesk.Revit.DB.Electrical.ConduitType");
        var conduit = assembly.GetType("Autodesk.Revit.DB.Electrical.Conduit");
        if (conduitType is null || conduit is null) return null;
        if (!conduitType.IsInstanceOfType(type)) return null;

        var createMethod = conduit.GetMethod(
            "Create",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(Document), typeof(ElementId), typeof(XYZ), typeof(XYZ), typeof(ElementId) },
            null);
        if (createMethod is null) return null;

        try
        {
            return createMethod.Invoke(null, new object[] { doc, type.Id, start, end, level.Id }) as Element;
        }
        catch (TargetInvocationException tex)
        {
            SmartConLogger.Warn($"{tex.InnerException?.Message ?? tex.Message}");
            return null;
        }
    }

    private static Element? PlaceCableTray(Document doc, Element type, Level level, XYZ start, XYZ end)
    {
        var assembly = doc.GetType().Assembly;
        var cableTrayType = assembly.GetType("Autodesk.Revit.DB.Electrical.CableTrayType");
        var cableTray = assembly.GetType("Autodesk.Revit.DB.Electrical.CableTray");
        if (cableTrayType is null || cableTray is null) return null;
        if (!cableTrayType.IsInstanceOfType(type)) return null;

        var createMethod = cableTray.GetMethod(
            "Create",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(Document), typeof(ElementId), typeof(XYZ), typeof(XYZ), typeof(ElementId) },
            null);
        if (createMethod is null) return null;

        try
        {
            return createMethod.Invoke(null, new object[] { doc, type.Id, start, end, level.Id }) as Element;
        }
        catch (TargetInvocationException tex)
        {
            SmartConLogger.Warn($"{tex.InnerException?.Message ?? tex.Message}");
            return null;
        }
    }

    private static Element? PlaceWall(Document doc, Element type, Level level, XYZ start, XYZ end)
    {
        if (type is not WallType wallType) return null;
        var line = Line.CreateBound(start, end);
        var height = RevitUnitsCompat.MetersToInternal(3.0);
        return Wall.Create(doc, line, wallType.Id, level.Id, height, 0.0, false, false);
    }
}

