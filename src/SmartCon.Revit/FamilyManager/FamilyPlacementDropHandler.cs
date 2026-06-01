using Autodesk.Revit.UI;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

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
    private readonly IProjectFamilyUsageRepository _usageRepo;
    private readonly int _targetRevitVersion;
    private readonly Action? _onCompleted;

    public FamilyPlacementDropHandler(
        IFamilySearchService searchService,
        IFamilyFileResolver fileResolver,
        IFamilyLoadService loadService,
        IFamilyPlacementService placementService,
        IProjectFamilyUsageRepository usageRepo,
        int targetRevitVersion,
        Action? onCompleted = null)
    {
        _searchService = searchService;
        _fileResolver = fileResolver;
        _loadService = loadService;
        _placementService = placementService;
        _usageRepo = usageRepo;
        _targetRevitVersion = targetRevitVersion;
        _onCompleted = onCompleted;
    }

    public void Execute(UIDocument document, object data)
    {
        try
        {
            if (data is not FamilyPlacementDragData dragData)
                return;

            var familyName = dragData.FamilyName;
            var typeName = dragData.TypeName;
            FamilyResolvedFile? resolved = null;

            if (!_searchService.IsFamilyLoaded(familyName))
            {
                resolved = Task.Run(() => _fileResolver
                    .ResolveForLoadAsync(dragData.CatalogItemId, _targetRevitVersion, CancellationToken.None))
                    .GetAwaiter().GetResult();

                if (string.IsNullOrEmpty(resolved.AbsolutePath))
                {
                    SmartConLogger.Warn($"FamilyPlacementDropHandler: No file resolved for '{familyName}'");
                    return;
                }

                var options = FamilyLoadOptions.Default with { PreferredName = familyName };
                var result = _loadService.LoadFamilyAsync(resolved, options, CancellationToken.None).GetAwaiter().GetResult();

                if (!result.Success)
                {
                    SmartConLogger.Warn($"FamilyPlacementDropHandler: Failed to load '{familyName}' — {result.ErrorMessage}");
                    return;
                }
            }

            _placementService.ActivateAndPlaceType(familyName, typeName);

            if (resolved is not null)
            {
                RecordUsage(document, dragData, resolved);
            }
            // Family already loaded — skip recording to avoid overwriting version with null

            _onCompleted?.Invoke();
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"FamilyPlacementDropHandler.Execute failed: {ex}");
        }
    }

    private void RecordUsage(UIDocument document, FamilyPlacementDragData dragData, FamilyResolvedFile? resolved)
    {
        try
        {
            var projectPath = document.Document.PathName;

            var usage = new ProjectFamilyUsage(
                Id: Guid.NewGuid().ToString(),
                CatalogItemId: dragData.CatalogItemId,
                VersionId: resolved?.VersionId,
                LoadedVersionLabel: resolved?.VersionLabel,
                ProjectName: "Active Project",
                ProjectPath: projectPath,
                RevitMajorVersion: _targetRevitVersion,
                Action: "Place",
                CreatedAtUtc: DateTimeOffset.UtcNow);

            Task.Run(() => _usageRepo.RecordUsageAsync(usage, CancellationToken.None)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"FamilyPlacementDropHandler: Failed to record usage — {ex.Message}");
        }
    }
}
