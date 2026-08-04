using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Context;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Placement entry point for system types (Issue #104). Since the sync
/// feature, placement never copies elements between documents: an up-to-date
/// type (ES marker matches the catalog) starts placement immediately;
/// otherwise the type is synchronized first via
/// <see cref="ISystemTypeSyncOrchestrator"/> and placement starts with the
/// already-updated type.
/// Categories that cannot be placed interactively (insulations require a
/// host — <c>UIDocument.CanPlaceElementType</c> is false and
/// <c>PostRequestForElementTypePlacement</c> throws
/// <see cref="Autodesk.Revit.Exceptions.ArgumentException"/>) are reported
/// as <see cref="SystemPlacementResult.LoadedManualPlacementRequired"/>:
/// the synchronized type stays in the project and the user places it
/// manually instead of crashing the command.
/// </summary>
public sealed class SystemFamilyPlacementService : ISystemFamilyPlacementService
{
    private readonly IRevitUIContext _revitUIContext;
    private readonly ISystemTypeSyncOrchestrator _syncOrchestrator;
    private readonly ISystemTypeFinder _typeFinder;
    private readonly IFamilyCatalogProvider _catalog;
    private readonly ITransactionService _transactionService;

    public SystemFamilyPlacementService(
        IRevitUIContext revitUIContext,
        ISystemTypeSyncOrchestrator syncOrchestrator,
        ISystemTypeFinder typeFinder,
        IFamilyCatalogProvider catalog,
        ITransactionService transactionService)
    {
        _revitUIContext = revitUIContext;
        _syncOrchestrator = syncOrchestrator;
        _typeFinder = typeFinder;
        _catalog = catalog;
        _transactionService = transactionService;
    }

    public SystemPlacementResult LoadAndPlaceSystemType(
        string catalogItemId, string typeName, int targetRevitVersion, string? familyName = null, string? familyKey = null)
    {
        var uiApp = _revitUIContext.GetUIApplication();
        var activeDoc = _revitUIContext.GetUIDocument().Document;

        if (uiApp is null || activeDoc is null)
        {
            SmartConLogger.Error("SystemFamilyPlacement.ABORT: uiApp or activeDoc is null");
            return SystemPlacementResult.Failed;
        }

        if (_syncOrchestrator.IsProjectTypeCurrent(activeDoc, catalogItemId, typeName, targetRevitVersion, familyName, familyKey))
        {
            SmartConLogger.Debug(
                $"SystemFamilyPlacement: type '{typeName}' is up-to-date (marker match), activating placement.");
            return ActivatePlacementByName(uiApp, activeDoc, typeName, catalogItemId, familyName, familyKey);
        }

        var result = _syncOrchestrator.SyncTypes(
            activeDoc, catalogItemId, new[] { new SystemTypeRef(typeName, familyName, familyKey) }, targetRevitVersion);

        var typeResult = result.TypeResults.Count > 0 ? result.TypeResults[0] : null;
        if (typeResult is null || !typeResult.IsSuccess)
        {
            SmartConLogger.Error(
                $"SystemFamilyPlacement: sync failed for '{typeName}': " +
                $"{typeResult?.ErrorMessage ?? "no result"}");
            return SystemPlacementResult.Failed;
        }

        return ActivatePlacementByName(uiApp, activeDoc, typeName, catalogItemId, familyName, familyKey);
    }

    private SystemPlacementResult ActivatePlacementByName(
        UIApplication uiApp, Document activeDoc, string typeName, string catalogItemId, string? familyName, string? familyKey = null)
    {
        var categoryOrdinal = ResolveCategoryOrdinal(catalogItemId);
        var typeId = _typeFinder.FindTypeByName(activeDoc, typeName, categoryOrdinal, familyName, familyKey);
        var elementType = typeId is not null ? activeDoc.GetElement(typeId) as ElementType : null;
        if (elementType is null)
        {
            SmartConLogger.Error(
                $"SystemFamilyPlacement: type '{typeName}' not found in the active project after sync");
            return SystemPlacementResult.Failed;
        }

        var uidoc = uiApp.ActiveUIDocument;
        if (uidoc is null)
        {
            SmartConLogger.Error("SystemFamilyPlacement.ABORT: ActiveUIDocument is null");
            return SystemPlacementResult.Failed;
        }

        // Manual test 2026-08-04 (round 2): the category ordinal must come
        // from the synced element itself — the catalog row can carry a null
        // RevitCategoryId, in which case the switch below never matched and
        // the code silently fell through to the no-op PostRequest path for
        // sketch categories (the exact bug the PostCommand branch fixes).
        var elementCategoryOrdinal = GetCategoryOrdinal(elementType) ?? categoryOrdinal;

        // Manual test 2026-08-04 (#200 follow-up): PostRequestForElementTypePlacement
        // SILENTLY no-ops for sketch-based categories (floors, roofs, stairs,
        // railings) — CanPlaceElementType returns true, the request is queued,
        // and Revit never enters placement (the user sees nothing). The native
        // activation for these categories is the tool command with the type
        // preselected as the project default (SetDefaultElementTypeId +
        // PostCommand — the documented workaround, Autodesk forums).
        // Round 2: PostCommand is deferred to a one-shot Idling handler —
        // IDropHandler.Execute is not a regular command context, and a
        // command posted from inside the drop is never started by Revit.
        if (TryGetPostCommandActivation(elementCategoryOrdinal, out var typeGroup, out var postableCommand))
        {
            try
            {
                _transactionService.RunInTransaction(activeDoc, $"Set default type '{typeName}'", d =>
                {
                    d.SetDefaultElementTypeId(typeGroup, elementType.Id);
                });

                var commandId = RevitCommandId.LookupPostableCommandId(postableCommand);
                SmartConLogger.Debug(
                    $"SystemFamilyPlacement: sketch category '{elementType.Category?.Name}' — default type set " +
                    $"to '{typeName}' (group {typeGroup}), deferring PostCommand({postableCommand}) to Idling.");

                EventHandler<IdlingEventArgs>? handler = null;
                handler = (s, e) =>
                {
                    uiApp.Idling -= handler;
                    try
                    {
                        uiApp.PostCommand(commandId);
                        SmartConLogger.Debug(
                            $"SystemFamilyPlacement: PostCommand({postableCommand}) posted from Idling for '{typeName}'.");
                    }
                    catch (Exception ex)
                    {
                        SmartConLogger.Warn(
                            $"SystemFamilyPlacement: PostCommand({postableCommand}) of '{typeName}' failed: {ex.Message} " +
                            "[Action: place the type manually in the Revit UI]");
                    }
                };
                uiApp.Idling += handler;
                return SystemPlacementResult.Placed;
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"SystemFamilyPlacement: PostCommand activation of '{typeName}' failed: {ex.Message} " +
                    "[Action: place the type manually in the Revit UI]");
                return SystemPlacementResult.LoadedManualPlacementRequired;
            }
        }

        SmartConLogger.Debug(
            $"SystemFamilyPlacement: linear/host category '{elementType.Category?.Name}' — " +
            $"using PostRequestForElementTypePlacement for '{typeName}'.");

        if (!uidoc.CanPlaceElementType(elementType))
        {
            SmartConLogger.Info(
                $"SystemFamilyPlacement: type '{typeName}' is loaded but cannot be placed " +
                "interactively (host-dependent category) — manual placement required");
            return SystemPlacementResult.LoadedManualPlacementRequired;
        }

        try
        {
            uidoc.PostRequestForElementTypePlacement(elementType);
            return SystemPlacementResult.Placed;
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException ex)
        {
            SmartConLogger.Warn(
                $"SystemFamilyPlacement: PostRequestForElementTypePlacement rejected '{typeName}': " +
                $"{ex.Message} [Action: place the type manually in the Revit UI]");
            return SystemPlacementResult.LoadedManualPlacementRequired;
        }
    }

    /// <summary>
    /// Sketch-based categories (floors, roofs, stairs, railings) whose
    /// native activation goes through the tool command:
    /// <c>SetDefaultElementTypeId</c> preselects the type, then
    /// <c>PostCommand</c> starts the tool. <c>PostRequestForElementTypePlacement</c>
    /// silently no-ops for them (manual test 2026-08-04).
    /// </summary>
    private static bool TryGetPostCommandActivation(
        int? categoryOrdinal, out ElementTypeGroup typeGroup, out PostableCommand command)
    {
        typeGroup = default;
        command = default;
        switch (categoryOrdinal)
        {
            case (int)BuiltInCategory.OST_Floors:
                typeGroup = ElementTypeGroup.FloorType;
                command = PostableCommand.ArchitecturalFloor;
                return true;
            case (int)BuiltInCategory.OST_Roofs:
                typeGroup = ElementTypeGroup.RoofType;
                command = PostableCommand.RoofByFootprint;
                return true;
            case (int)BuiltInCategory.OST_Stairs:
                typeGroup = ElementTypeGroup.StairsType;
                command = PostableCommand.Stair;
                return true;
            case (int)BuiltInCategory.OST_StairsRailing:
                typeGroup = ElementTypeGroup.StairsRailingType;
                command = PostableCommand.Railing;
                return true;
            default:
                return false;
        }
    }

    private static int? GetCategoryOrdinal(ElementType elementType)
    {
        try
        {
            var builtIn = Core.Compatibility.CategoryCompat.GetBuiltInCategory(elementType.Category);
            return builtIn == BuiltInCategory.INVALID ? null : (int)builtIn;
        }
        catch
        {
            return null;
        }
    }

    private int? ResolveCategoryOrdinal(string catalogItemId)
    {
        try
        {
            var item = Core.Threading.AsyncBridge.RunSync(
                () => _catalog.GetItemAsync(catalogItemId, CancellationToken.None));
            return item?.RevitCategoryId;
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"SystemFamilyPlacement: category resolve failed for '{catalogItemId}': {ex.Message}");
            return null;
        }
    }
}
