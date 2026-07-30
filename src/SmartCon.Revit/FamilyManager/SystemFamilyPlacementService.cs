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

    public void LoadAndPlaceSystemType(string catalogItemId, string typeName, int targetRevitVersion)
    {
        var uiApp = _revitUIContext.GetUIApplication();
        var activeDoc = _revitUIContext.GetUIDocument().Document;

        if (uiApp is null || activeDoc is null)
        {
            SmartConLogger.Error("SystemFamilyPlacement.ABORT: uiApp or activeDoc is null");
            return;
        }

        if (_syncOrchestrator.IsProjectTypeCurrent(activeDoc, catalogItemId, typeName, targetRevitVersion))
        {
            SmartConLogger.Debug(
                $"SystemFamilyPlacement: type '{typeName}' is up-to-date (marker match), activating placement.");
            ActivatePlacementByName(uiApp, activeDoc, typeName, catalogItemId);
            return;
        }

        var result = _syncOrchestrator.SyncTypes(
            activeDoc, catalogItemId, new[] { typeName }, targetRevitVersion);

        var typeResult = result.TypeResults.Count > 0 ? result.TypeResults[0] : null;
        if (typeResult is null || !typeResult.IsSuccess)
        {
            SmartConLogger.Error(
                $"SystemFamilyPlacement: sync failed for '{typeName}': " +
                $"{typeResult?.ErrorMessage ?? "no result"}");
            return;
        }

        ActivatePlacementByName(uiApp, activeDoc, typeName, catalogItemId);
    }

    private void ActivatePlacementByName(
        UIApplication uiApp, Document activeDoc, string typeName, string catalogItemId)
    {
        var categoryOrdinal = ResolveCategoryOrdinal(catalogItemId);
        var typeId = _typeFinder.FindTypeByName(activeDoc, typeName, categoryOrdinal);
        var elementType = typeId is not null ? activeDoc.GetElement(typeId) as ElementType : null;
        if (elementType is null)
        {
            SmartConLogger.Error(
                $"SystemFamilyPlacement: type '{typeName}' not found in the active project after sync");
            return;
        }

        uiApp.ActiveUIDocument?.PostRequestForElementTypePlacement(elementType);
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
