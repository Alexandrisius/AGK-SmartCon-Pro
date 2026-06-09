using Autodesk.Revit.UI;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services;
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
    private readonly ISystemFamilyPlacementService _systemFamilyPlacementService;
    private readonly IProjectFamilyUsageRepository _usageRepo;
    private readonly IWindowFocusService? _windowFocusService;

    public event Action? PlacementCompleted;
    public event Action<string>? PlacementFailed;
    public event Action<string>? PlacementSucceeded;
    public event Action<string>? PlacementStatusMessage;

    public RevitFamilyPlacementDragService(
        IRevitUIContext revitUIContext,
        IFamilySearchService searchService,
        IFamilyFileResolver fileResolver,
        IFamilyLoadService loadService,
        IFamilyPlacementService placementService,
        ISystemFamilyPlacementService systemFamilyPlacementService,
        IProjectFamilyUsageRepository usageRepo,
        IWindowFocusService? windowFocusService = null)
    {
        _revitUIContext = revitUIContext;
        _searchService = searchService;
        _fileResolver = fileResolver;
        _loadService = loadService;
        _placementService = placementService;
        _systemFamilyPlacementService = systemFamilyPlacementService;
        _usageRepo = usageRepo;
        _windowFocusService = windowFocusService;
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
                _systemFamilyPlacementService,
                _usageRepo,
                data.TargetRevitVersion,
                OnPlacementCompleted,
                OnPlacementFailed,
                OnPlacementSucceeded,
                OnPlacementStatusMessage);

            UIApplication.DoDragDrop(data, handler);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"RevitFamilyPlacementDragService.DoDragDrop: failed: {ex}");
        }
    }

    private void OnPlacementCompleted()
    {
        PlacementCompleted?.Invoke();
        _windowFocusService?.RestoreFocusAndRefreshUI();
    }

    private void OnPlacementFailed(string errorMessage)
    {
        PlacementFailed?.Invoke(errorMessage);
    }

    private void OnPlacementSucceeded(string successMessage)
    {
        PlacementSucceeded?.Invoke(successMessage);
    }

    private void OnPlacementStatusMessage(string statusMessage)
    {
        PlacementStatusMessage?.Invoke(statusMessage);
    }
}
