using Autodesk.Revit.UI;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Context;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Revit implementation of IFamilyPlacementDragService.
/// Calls UIApplication.DoDragDrop with a custom IDropHandler.
/// </summary>
public sealed class RevitFamilyPlacementDragService : IFamilyPlacementDragService
{
    private readonly IRevitUIContext _revitUIContext;
    private readonly IFamilySearchService _searchService;
    private readonly IFamilyFileResolver _fileResolver;
    private readonly IFamilyLoadService _loadService;
    private readonly IFamilyPlacementService _placementService;
    private readonly IProjectFamilyUsageRepository _usageRepo;

    public event Action? PlacementCompleted;
    public event Action<string>? PlacementFailed;

    public RevitFamilyPlacementDragService(
        IRevitUIContext revitUIContext,
        IFamilySearchService searchService,
        IFamilyFileResolver fileResolver,
        IFamilyLoadService loadService,
        IFamilyPlacementService placementService,
        IProjectFamilyUsageRepository usageRepo)
    {
        _revitUIContext = revitUIContext;
        _searchService = searchService;
        _fileResolver = fileResolver;
        _loadService = loadService;
        _placementService = placementService;
        _usageRepo = usageRepo;
    }

    public void StartPlacementDrag(FamilyPlacementDragData data)
    {
        try
        {
            var uiApp = _revitUIContext.GetUIApplication();
            var handler = new FamilyPlacementDropHandler(
                _searchService,
                _fileResolver,
                _loadService,
                _placementService,
                _usageRepo,
                data.TargetRevitVersion,
                OnPlacementCompleted,
                OnPlacementFailed);

            UIApplication.DoDragDrop(data, handler);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"RevitFamilyPlacementDragService.DoDragDrop failed: {ex}");
        }
    }

    private void OnPlacementCompleted()
    {
        PlacementCompleted?.Invoke();
    }

    private void OnPlacementFailed(string errorMessage)
    {
        PlacementFailed?.Invoke(errorMessage);
    }
}
