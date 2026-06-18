using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Threading;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Revit IDropHandler that loads the family (if needed) and activates the requested type for placement.
/// Executed by Revit on the main thread when the user drops on the canvas.
/// </summary>
public sealed class FamilyPlacementDropHandler : IDropHandler
{
    private readonly IFamilySearchService _searchService;
    private readonly IFamilyFileResolver _fileResolver;
    private readonly IFamilyLoadService _loadService;
    private readonly IFamilyPlacementService _placementService;
    private readonly ISystemFamilyPlacementService _systemFamilyPlacementService;
    private readonly IFamilyVersionStore _versionStore;
    private readonly IStaleDetector _staleDetector;
    private readonly IClock _clock;
    private readonly int _targetRevitVersion;
    private readonly Action? _onCompleted;
    private readonly Action<string>? _onError;
    private readonly Action<string>? _onSuccess;
    private readonly Action<string>? _onStatusMessage;
    private readonly Func<SharedFamilyDecisionRequest, SharedFamiliesLoadChoice>? _onSharedDecision;

    public FamilyPlacementDropHandler(
        IFamilySearchService searchService,
        IFamilyFileResolver fileResolver,
        IFamilyLoadService loadService,
        IFamilyPlacementService placementService,
        ISystemFamilyPlacementService systemFamilyPlacementService,
        IFamilyVersionStore versionStore,
        IStaleDetector staleDetector,
        IClock clock,
        int targetRevitVersion,
        Action? onCompleted = null,
        Action<string>? onError = null,
        Action<string>? onSuccess = null,
        Action<string>? onStatusMessage = null,
        Func<SharedFamilyDecisionRequest, SharedFamiliesLoadChoice>? onSharedDecision = null)
    {
        _searchService = searchService;
        _fileResolver = fileResolver;
        _loadService = loadService;
        _placementService = placementService;
        _systemFamilyPlacementService = systemFamilyPlacementService;
        _versionStore = versionStore;
        _staleDetector = staleDetector;
        _clock = clock;
        _targetRevitVersion = targetRevitVersion;
        _onCompleted = onCompleted;
        _onError = onError;
        _onSuccess = onSuccess;
        _onStatusMessage = onStatusMessage;
        _onSharedDecision = onSharedDecision;
    }

    public void Execute(UIDocument document, object data)
    {
        using var _scope = SmartConLogger.BeginScope("DropHandler",
            ("Method", "Execute"));

        try
        {
            if (data is not FamilyPlacementDragData dragData)
                return;

            if (dragData.FamilySource == "system" || !string.IsNullOrEmpty(dragData.UniqueId))
            {
                SmartConLogger.Info($"System family: '{dragData.FamilyName}', type: '{dragData.TypeName}' (FamilySource='{dragData.FamilySource}', UniqueId='{dragData.UniqueId}')");
                _systemFamilyPlacementService.LoadAndPlaceSystemType(
                    dragData.CatalogItemId,
                    dragData.TypeName,
                    dragData.TargetRevitVersion);

                _onSuccess?.Invoke($"Системный тип '{dragData.TypeName}' скопирован и активирован");
                _onCompleted?.Invoke();
                return;
            }

            var familyName = dragData.FamilyName;
            var typeName = dragData.TypeName;
            FamilyResolvedFile? resolved = null;

            var isFamilyLoaded = _searchService.IsFamilyLoaded(familyName);
            var isTypeLoaded = isFamilyLoaded && _searchService.HasFamilyType(familyName, typeName);
            SmartConLogger.Info($"Family '{familyName}' loaded: {isFamilyLoaded}, Type '{typeName}' loaded: {isTypeLoaded}");

            if (!isFamilyLoaded || !isTypeLoaded)
            {
                resolved = AsyncBridge.RunSync(() => _fileResolver
                    .ResolveForLoadAsync(dragData.CatalogItemId, _targetRevitVersion, CancellationToken.None));

                if (string.IsNullOrEmpty(resolved.AbsolutePath))
                {
                    SmartConLogger.Warn($"No file resolved for '{familyName}'");
                    return;
                }

                FamilyLoadResult result;
                if (dragData.IsVirtual)
                {
                    var options = FamilyLoadOptions.Default with { PreferredName = familyName };
                    result = _loadService.LoadFamilyAsync(
                        resolved,
                        options,
                        onStatusMessage: _onStatusMessage,
                        onSharedDecision: _onSharedDecision,
                        ct: CancellationToken.None).GetAwaiter().GetResult();
                }
                else
                {
                    result = _loadService.LoadFamilySymbolAsync(
                        resolved.AbsolutePath,
                        typeName,
                        onStatusMessage: _onStatusMessage,
                        onSharedDecision: _onSharedDecision,
                        ct: CancellationToken.None).GetAwaiter().GetResult();
                }

                if (!result.Success)
                {
                    var errorMsg = $"Failed to load '{familyName}': {result.ErrorMessage}";
                    SmartConLogger.Warn(errorMsg);
                    _onError?.Invoke(errorMsg);
                    return;
                }

                SmartConLogger.Info($"Loaded successfully: {result.Status} - {result.Message}");
            }

            var placementSuccess = _placementService.ActivateAndPlaceType(familyName, typeName);
            if (!placementSuccess)
            {
                var errorMsg = $"Failed to activate type '{typeName}' for placement";
                SmartConLogger.Warn(errorMsg);
                _onError?.Invoke(errorMsg);
                return;
            }

            if (resolved is not null)
            {
                WriteVersionMarker(document.Document, dragData, resolved);
                _onSuccess?.Invoke($"Семейство '{familyName}' загружено и активировано для размещения");
            }
            else
            {
                _onSuccess?.Invoke($"Тип '{typeName}' активирован (семейство уже загружено)");
            }

            _onCompleted?.Invoke();
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"FamilyPlacementDropHandler.Execute failed: {ex}");
        }
    }

    private void WriteVersionMarker(Document document, FamilyPlacementDragData dragData, FamilyResolvedFile? resolved)
    {
        try
        {
            var familyName = dragData.FamilyName;
            var loadedFamily = FindFamilyByName(document, familyName);
            if (loadedFamily is null) return;

            var version = new FamilyVersion(
                SchemaVersion: FamilyVersion.CurrentSchemaVersion,
                CatalogItemId: dragData.CatalogItemId,
                VersionLabel: resolved?.VersionLabel ?? string.Empty,
                LoadedAtUtc: _clock.UtcNow,
                SourceRevitVersion: _targetRevitVersion);

            _versionStore.WriteToLoadedFamily(document, loadedFamily.Id, version);
            _staleDetector.InvalidateCache();
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"FamilyPlacementDropHandler: Failed to write FamilyVersion marker — {ex.Message}");
        }
    }

    private static Autodesk.Revit.DB.Family? FindFamilyByName(Document document, string familyName)
    {
        if (document is null || string.IsNullOrEmpty(familyName)) return null;
        using var collector = new FilteredElementCollector(document).OfClass(typeof(Autodesk.Revit.DB.Family));
        foreach (Autodesk.Revit.DB.Family f in collector)
        {
            if (string.Equals(f.Name, familyName, StringComparison.Ordinal)) return f;
        }
        return null;
    }
}
