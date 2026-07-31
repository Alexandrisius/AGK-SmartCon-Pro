using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
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

    public SystemFamilyPlacementService(
        IRevitUIContext revitUIContext,
        ISystemTypeSyncOrchestrator syncOrchestrator,
        ISystemTypeFinder typeFinder,
        IFamilyCatalogProvider catalog)
    {
        _revitUIContext = revitUIContext;
        _syncOrchestrator = syncOrchestrator;
        _typeFinder = typeFinder;
        _catalog = catalog;
    }

    public SystemPlacementResult LoadAndPlaceSystemType(
        string catalogItemId, string typeName, int targetRevitVersion, string? familyName = null)
    {
        var uiApp = _revitUIContext.GetUIApplication();
        var activeDoc = _revitUIContext.GetUIDocument().Document;

        if (uiApp is null || activeDoc is null)
        {
            SmartConLogger.Error("SystemFamilyPlacement.ABORT: uiApp or activeDoc is null");
            return SystemPlacementResult.Failed;
        }

        if (_syncOrchestrator.IsProjectTypeCurrent(activeDoc, catalogItemId, typeName, targetRevitVersion, familyName))
        {
            SmartConLogger.Debug(
                $"SystemFamilyPlacement: type '{typeName}' is up-to-date (marker match), activating placement.");
            return ActivatePlacementByName(uiApp, activeDoc, typeName, catalogItemId, familyName);
        }

        var result = _syncOrchestrator.SyncTypes(
            activeDoc, catalogItemId, new[] { new SystemTypeRef(typeName, familyName) }, targetRevitVersion);

        var typeResult = result.TypeResults.Count > 0 ? result.TypeResults[0] : null;
        if (typeResult is null || !typeResult.IsSuccess)
        {
            SmartConLogger.Error(
                $"SystemFamilyPlacement: sync failed for '{typeName}': " +
                $"{typeResult?.ErrorMessage ?? "no result"}");
            return SystemPlacementResult.Failed;
        }

        return ActivatePlacementByName(uiApp, activeDoc, typeName, catalogItemId, familyName);
    }

    private SystemPlacementResult ActivatePlacementByName(
        UIApplication uiApp, Document activeDoc, string typeName, string catalogItemId, string? familyName)
    {
        var categoryOrdinal = ResolveCategoryOrdinal(catalogItemId);
        var typeId = _typeFinder.FindTypeByName(activeDoc, typeName, categoryOrdinal, familyName);
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
