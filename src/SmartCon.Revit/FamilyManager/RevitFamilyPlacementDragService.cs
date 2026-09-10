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
    private readonly IFamilyVersionStore _versionStore;
    private readonly IStaleDetector _staleDetector;
    private readonly IClock _clock;
    private readonly IWindowFocusService? _windowFocusService;
    private readonly ISharedNestedFamilyRepository? _nestedSharedRepository;
    private readonly IFamilyDependencyRepository? _dependencyRepository;
    private readonly IFamilyCatalogProvider? _catalogProvider;

    public event Action? PlacementCompleted;
    public event Action<FamilyPlacementDragData>? SystemTypePlaced;
    public event Action<string>? PlacementFailed;
    public event Action<string>? PlacementSucceeded;
    public event Action<string>? PlacementStatusMessage;
    public event Func<SharedFamilyDecisionRequest, SharedFamiliesLoadChoice>? SharedFamilyDecisionRequested;

    public RevitFamilyPlacementDragService(
        IRevitUIContext revitUIContext,
        IFamilySearchService searchService,
        IFamilyFileResolver fileResolver,
        IFamilyLoadService loadService,
        IFamilyPlacementService placementService,
        ISystemFamilyPlacementService systemFamilyPlacementService,
        IFamilyVersionStore versionStore,
        IStaleDetector staleDetector,
        IClock clock,
        IWindowFocusService? windowFocusService = null,
        ISharedNestedFamilyRepository? nestedSharedRepository = null,
        IFamilyDependencyRepository? dependencyRepository = null,
        IFamilyCatalogProvider? catalogProvider = null)
    {
        _revitUIContext = revitUIContext;
        _searchService = searchService;
        _fileResolver = fileResolver;
        _loadService = loadService;
        _placementService = placementService;
        _systemFamilyPlacementService = systemFamilyPlacementService;
        _versionStore = versionStore;
        _staleDetector = staleDetector;
        _clock = clock;
        _windowFocusService = windowFocusService;
        _nestedSharedRepository = nestedSharedRepository;
        _dependencyRepository = dependencyRepository;
        _catalogProvider = catalogProvider;
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
                _versionStore,
                _staleDetector,
                _clock,
                data.TargetRevitVersion,
                OnPlacementCompleted,
                OnPlacementFailed,
                OnPlacementSucceeded,
                OnPlacementStatusMessage,
                OnSharedFamilyDecisionRequested,
                _nestedSharedRepository,
                OnSystemTypePlaced,
                _dependencyRepository,
                _catalogProvider);

            UIApplication.DoDragDrop(data, handler);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"RevitFamilyPlacementDragService.DoDragDrop: failed: {ex}");
        }
    }

    private void OnSystemTypePlaced(FamilyPlacementDragData data)
    {
        SystemTypePlaced?.Invoke(data);
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

    private SharedFamiliesLoadChoice OnSharedFamilyDecisionRequested(SharedFamilyDecisionRequest request)
    {
        return SharedFamilyDecisionRequested?.Invoke(request) ?? SharedFamiliesLoadChoice.UseProject;
    }
}
