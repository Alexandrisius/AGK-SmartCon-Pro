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
    private readonly ISharedNestedFamilyRepository? _nestedSharedRepository;
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
        Func<SharedFamilyDecisionRequest, SharedFamiliesLoadChoice>? onSharedDecision = null,
        ISharedNestedFamilyRepository? nestedSharedRepository = null)
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
        _nestedSharedRepository = nestedSharedRepository;
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
                var placed = _systemFamilyPlacementService.LoadAndPlaceSystemType(
                    dragData.CatalogItemId,
                    dragData.TypeName,
                    dragData.TargetRevitVersion);

                if (placed)
                {
                    // Issue #104: the just-synced type carries a fresh ES
                    // marker — prune the item from the stale snapshot so its
                    // badge clears immediately (same as the loadable path in
                    // WriteVersionMarker). The next "Проверить" re-evaluates
                    // the item's other types when the family has several.
                    _staleDetector.MarkUpdated([dragData.CatalogItemId]);
                    _onSuccess?.Invoke($"Системный тип '{dragData.TypeName}' скопирован и активирован");
                }
                else
                {
                    _onError?.Invoke($"Не удалось синхронизировать системный тип '{dragData.TypeName}'");
                }
                _onCompleted?.Invoke();
                return;
            }

            var familyName = dragData.FamilyName;
            var typeName = dragData.TypeName;
            FamilyResolvedFile? resolved = null;

            var isFamilyLoaded = _searchService.IsFamilyLoaded(familyName);
            var isTypeLoaded = isFamilyLoaded && _searchService.HasFamilyType(familyName, typeName);
            SmartConLogger.Info($"Family '{familyName}' loaded: {isFamilyLoaded}, Type '{typeName}' loaded: {isTypeLoaded}");

            if (isFamilyLoaded)
            {
                var projectFamily = FindFamilyByName(document.Document, familyName);
                if (projectFamily is not null)
                {
                    var marker = _versionStore.ReadFromLoadedFamily(document.Document, projectFamily.Id);
                    var markerCatalogItemId = marker?.CatalogItemId ?? "<none>";
                    var catalogMatch = string.Equals(markerCatalogItemId, dragData.CatalogItemId, StringComparison.OrdinalIgnoreCase);
                    SmartConLogger.Info(
                        $"Drop identity check: {RevitFamilySearchService.DescribeFamily(projectFamily)}, " +
                        $"dragCatalogItemId='{dragData.CatalogItemId}', markerCatalogItemId='{markerCatalogItemId}', " +
                        $"markerVersionLabel='{marker?.VersionLabel ?? "<none>"}', catalogMatch={catalogMatch}");
                }
            }

            if (!isFamilyLoaded || !isTypeLoaded)
            {
                resolved = AsyncBridge.RunSync(() => _fileResolver
                    .ResolveForLoadAsync(dragData.CatalogItemId, _targetRevitVersion, CancellationToken.None));

                SmartConLogger.Info(
                    $"Drop resolved file: path='{resolved?.AbsolutePath}', versionLabel='{resolved?.VersionLabel}', " +
                    $"catalogItemId='{dragData.CatalogItemId}', isVirtual={dragData.IsVirtual}");

                if (resolved is null || string.IsNullOrEmpty(resolved.AbsolutePath))
                {
                    SmartConLogger.Warn($"No file resolved for '{familyName}' [Action: проверьте, что для этой версии Revit в каталоге есть файл семейства; при необходимости выполните миграцию данных]");
                    return;
                }

                FamilyLoadResult result;
                // Pre-resolve shared-nested names via AsyncBridge (pure SQLite —
                // SAFE per AsyncBridge docs) BEFORE the blocking load calls.
                // Execute() runs on the Revit main thread and blocks on
                // .GetAwaiter().GetResult(); passing pre-resolved names removes
                // the internal SQLite await from LoadFamilyAsync /
                // LoadFamilySymbolAsync so the whole load completes
                // synchronously (latent-deadlock hardening, same as
                // StaleFamilyUpdater).
                IReadOnlyList<string>? nestedNames = null;
                if (_nestedSharedRepository is not null)
                {
                    try
                    {
                        nestedNames = AsyncBridge.RunSync(() => _nestedSharedRepository
                            .GetNamesForCurrentVersionAsync(dragData.CatalogItemId, CancellationToken.None));
                    }
                    catch (Exception ex)
                    {
                        SmartConLogger.Warn(
                            $"Failed to pre-resolve nested names for '{dragData.CatalogItemId}': {ex.Message} " +
                            "[Action: continuing without fallback names — dialog may show placeholder in Revit 2023/2024.2]");
                    }
                }

                if (dragData.IsVirtual)
                {
                    var options = FamilyLoadOptions.Default with { PreferredName = familyName };
                    result = _loadService.LoadFamilyAsync(
                        resolved,
                        options,
                        onStatusMessage: _onStatusMessage,
                        onSharedDecision: _onSharedDecision,
                        nestedSharedNames: nestedNames,
                        ct: CancellationToken.None).GetAwaiter().GetResult();
                }
                else
                {
                    result = _loadService.LoadFamilySymbolAsync(
                        resolved.AbsolutePath,
                        typeName,
                        onStatusMessage: _onStatusMessage,
                        onSharedDecision: _onSharedDecision,
                        nestedSharedNames: nestedNames,
                        catalogItemId: dragData.CatalogItemId,
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
            // Targeted drop: only the placed family leaves the snapshot, every
            // other stale entry is preserved. The InvalidateCache() we used
            // before wiped the entire session snapshot, so a user who
            // previously ran Check on a different category would have lost
            // all their staleness markers until the next Check.
            _staleDetector.MarkUpdated([dragData.CatalogItemId]);
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
            // Case-insensitive to match the rest of the project
            // (IFamilyFinder.FindByName, IFamilySearchService) so a family
            // loaded with different casing still receives the marker.
            if (string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase)) return f;
        }
        return null;
    }
}
