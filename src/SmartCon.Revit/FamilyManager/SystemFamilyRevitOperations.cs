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

public sealed partial class SystemFamilyRevitOperations : ISystemFamilyRevitOperations
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
