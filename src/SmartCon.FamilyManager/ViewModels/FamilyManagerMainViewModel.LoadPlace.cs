using System.IO;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyManagerMainViewModel
{
    [RelayCommand(CanExecute = nameof(CanLoadToProject))]
    private void LoadToProject()
    {
        if (SelectedItem is null) return;

        var selectedId = SelectedItem.Id;
        var selectedName = SelectedItem.Name;
        var targetRevit = CurrentRevitVersion;

        _externalEvent.Raise(() =>
        {
            try
            {
                var resolved = Task.Run(() => _fileResolver.ResolveForLoadAsync(selectedId, targetRevit, CancellationToken.None)).GetAwaiter().GetResult();

                if (string.IsNullOrEmpty(resolved.AbsolutePath))
                {
                    StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_NoVersionSelected) ?? "No version available for this Revit version";
                    return;
                }

                var loadOptions = FamilyLoadOptions.Default with { PreferredName = selectedName };
                var result = _loadService.LoadFamilyAsync(resolved, loadOptions, CancellationToken.None).GetAwaiter().GetResult();

                if (result.Success)
                {
                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_LoadSuccess) ?? "Family \"{0}\" loaded",
                        result.FamilyName ?? selectedName);

                    var familyName = result.FamilyName ?? selectedName;
                    var typeNames = _familySearchService.GetFamilyTypeNames(familyName);
                    if (typeNames.Count > 0)
                        FireAndForget(() => SaveTypesAndReloadTreeAsync(selectedId, typeNames.ToList()));

                    var usage = new ProjectFamilyUsage(
                        Id: Guid.NewGuid().ToString(),
                        CatalogItemId: selectedId,
                        VersionId: resolved.VersionId,
                        ProjectName: "Active Project",
                        ProjectPath: string.Empty,
                        RevitMajorVersion: targetRevit,
                        Action: "Load",
                        CreatedAtUtc: DateTimeOffset.UtcNow);

                    FireAndForget(() => _usageRepo.RecordUsageAsync(usage, CancellationToken.None));
                }
                else
                {
                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_LoadError) ?? "Load error: {0}",
                        result.ErrorMessage);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_LoadError) ?? "Load error: {0}",
                    ex.Message);
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanLoadToProject))]
    private void LoadAndPlace()
    {
        if (SelectedItem is null) return;

        var selectedId = SelectedItem.Id;
        var selectedName = SelectedItem.Name;
        var targetRevit = CurrentRevitVersion;

        _externalEvent.Raise(() =>
        {
            try
            {
                SmartConLogger.FreezeThreadPool("LoadAndPlace.Start");

                var resolved = SmartConLogger.FreezeTimer("LoadAndPlace.ResolveFile", () =>
                    Task.Run(() => _fileResolver.ResolveForLoadAsync(selectedId, targetRevit, CancellationToken.None)).GetAwaiter().GetResult());

                if (string.IsNullOrEmpty(resolved.AbsolutePath))
                {
                    StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_NoVersionSelected) ?? "No version available";
                    SmartConLogger.Freeze("LoadAndPlace: No version available");
                    return;
                }

                var loadOptions = FamilyLoadOptions.Default with { PreferredName = selectedName };
                var result = SmartConLogger.FreezeTimer("LoadAndPlace.LoadFamily", () =>
                    _loadService.LoadFamilyAsync(resolved, loadOptions, CancellationToken.None).GetAwaiter().GetResult());

                if (!result.Success)
                {
                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_LoadError) ?? "Load error: {0}",
                        result.ErrorMessage);
                    SmartConLogger.Freeze($"LoadAndPlace: Load failed - {result.ErrorMessage}");
                    return;
                }

                SmartConLogger.Freeze($"LoadAndPlace: Family '{result.FamilyName}' loaded successfully");

                var familyName = result.FamilyName ?? selectedName;
                var typeNames = _familySearchService.GetFamilyTypeNames(familyName);
                var firstType = typeNames.Count > 0 ? typeNames[0] : null;

                if (firstType is not null)
                {
                    _familyPlacementService.ActivateAndPlaceType(familyName, firstType);
                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_LoadAndPlaceSuccess) ?? "Family \"{0}\" — click to place",
                        familyName);
                }
                else
                {
                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_LoadError) ?? "Load error: {0}",
                        LanguageManager.GetString(StringLocalization.Keys.FM_FamilyNotFoundAfterLoad) ?? "No types found after loading");
                }

                var usage = new ProjectFamilyUsage(
                    Id: Guid.NewGuid().ToString(),
                    CatalogItemId: selectedId,
                    VersionId: resolved.VersionId,
                    ProjectName: "Active Project",
                    ProjectPath: string.Empty,
                    RevitMajorVersion: targetRevit,
                    Action: "LoadAndPlace",
                    CreatedAtUtc: DateTimeOffset.UtcNow);

                FireAndForget(() => _usageRepo.RecordUsageAsync(usage, CancellationToken.None));
                SmartConLogger.Freeze("LoadAndPlace: Completed successfully");
            }
            catch (Exception ex)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_LoadError) ?? "Load error: {0}",
                    ex.Message);
                SmartConLogger.Freeze($"LoadAndPlace: Exception - {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    [RelayCommand]
    private void PlaceType()
    {
        if (SelectedTreeNode is not FamilyTypeNodeViewModel typeNode) return;

        var parent = FindParentOf(TreeNodes, typeNode);
        if (parent is not FamilyLeafNodeViewModel leaf) return;

        var catalogItemId = leaf.CatalogItemId;
        var familyName = leaf.DisplayName;
        var typeName = typeNode.TypeName;
        var targetRevit = CurrentRevitVersion;

        _externalEvent.Raise(() =>
        {
            try
            {
                SmartConLogger.FreezeThreadPool("PlaceType.Start");

                if (!_familySearchService.IsFamilyLoaded(familyName))
                {
                    var resolved = SmartConLogger.FreezeTimer("PlaceType.ResolveFile", () =>
                        Task.Run(() => _fileResolver.ResolveForLoadAsync(catalogItemId, targetRevit, CancellationToken.None)).GetAwaiter().GetResult());

                    if (string.IsNullOrEmpty(resolved.AbsolutePath))
                    {
                        SmartConLogger.Freeze("PlaceType: No file resolved");
                        return;
                    }

                    var loadOptions = FamilyLoadOptions.Default with { PreferredName = familyName };
                    SmartConLogger.FreezeTimer("PlaceType.LoadFamily", () =>
                        _loadService.LoadFamilyAsync(resolved, loadOptions, CancellationToken.None).GetAwaiter().GetResult());
                }
                else
                {
                    SmartConLogger.Freeze("PlaceType: Family already loaded");
                }

                _familyPlacementService.ActivateAndPlaceType(familyName, typeName);

                // Важно: читаем типы СИНХРОННО в ExternalEvent, передаем готовый список в FireAndForget
                var typeNames = _familySearchService.GetFamilyTypeNames(familyName).ToList();
                if (typeNames.Count > 0)
                {
                    FireAndForget(() => SaveTypesAndReloadTreeAsync(catalogItemId, typeNames));
                }
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn($"PlaceType failed: {ex.Message}");
            }
        });
    }
}
