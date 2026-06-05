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
    [RelayCommand(CanExecute = nameof(CanStartPlacementDrag))]
    private void StartPlacementDrag(object? item)
    {
        if (item is not FamilyTypeNodeViewModel typeNode) return;

        var parent = FindParentOf(TreeNodes, typeNode);
        if (parent is not FamilyLeafNodeViewModel leaf) return;

        var data = new FamilyPlacementDragData(
            leaf.CatalogItemId,
            leaf.DisplayName,
            typeNode.TypeName,
            CurrentRevitVersion,
            typeNode.IsVirtual,
            leaf.FamilySource,
            typeNode.UniqueId);

        _placementDragService.StartPlacementDrag(data);
    }

    private bool CanStartPlacementDrag(object? item)
    {
        if (item is not FamilyTypeNodeViewModel typeNode) return false;

        var parent = FindParentOf(TreeNodes, typeNode);
        if (parent is not FamilyLeafNodeViewModel leaf) return false;

        return leaf.ContentStatus == ContentStatus.Active && _accessControl.CanLoadToProject;
    }

    [RelayCommand(CanExecute = nameof(CanLoadToProject))]
    private async Task LoadToProject()
    {
        await ExecuteLoadOrUpdateAsync(overwriteParameterValues: true);
    }

    [RelayCommand(CanExecute = nameof(CanLoadToProject))]
    private async Task LoadToProjectKeepParams()
    {
        await ExecuteLoadOrUpdateAsync(overwriteParameterValues: false);
    }

    private async Task ExecuteLoadOrUpdateAsync(bool overwriteParameterValues)
    {
        if (SelectedItem is null) return;

        var selectedId = SelectedItem.Id;
        var selectedName = SelectedItem.Name;
        var targetRevit = CurrentRevitVersion;

        await _awaitableEvent.RaiseAsyncTask(async _ =>
        {
            try
            {
                var resolved = await _fileResolver.ResolveForLoadAsync(selectedId, targetRevit, CancellationToken.None).ConfigureAwait(true);

                if (string.IsNullOrEmpty(resolved.AbsolutePath))
                {
                    StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_NoVersionSelected) ?? "No version available for this Revit version";
                    return;
                }

                var loadOptions = FamilyLoadOptions.Default with { PreferredName = selectedName, OverwriteParameterValues = overwriteParameterValues };
                var result = await _loadService.LoadFamilyAsync(resolved, loadOptions, ct: CancellationToken.None).ConfigureAwait(true);

                if (result.Success)
                {
                    var loadedName = result.FamilyName ?? selectedName;

                    var msg = result.Status switch
                    {
                        FamilyLoadStatus.Loaded => string.Format(
                            LanguageManager.GetString(StringLocalization.Keys.FM_LoadSuccess) ?? "Family \"{0}\" loaded",
                            result.FamilyName ?? selectedName),
                        FamilyLoadStatus.Updated => string.Format(
                            LanguageManager.GetString(StringLocalization.Keys.FM_LoadSuccess) ?? "Family \"{0}\" updated to latest version",
                            result.FamilyName ?? selectedName),
                        FamilyLoadStatus.Current => string.Format(
                            LanguageManager.GetString(StringLocalization.Keys.FM_LoadSuccess) ?? "Family \"{0}\" is already up-to-date",
                            result.FamilyName ?? selectedName),
                        _ => string.Format(
                            LanguageManager.GetString(StringLocalization.Keys.FM_LoadSuccess) ?? "Family \"{0}\" loaded",
                            result.FamilyName ?? selectedName)
                    };
                    StatusMessage = msg;

                    var projectPath = _revitContext.GetDocument().PathName;
                    var loadedVersionLabel = SelectedItem?.VersionLabel;

                    var usage = new ProjectFamilyUsage(
                        Id: Guid.NewGuid().ToString(),
                        CatalogItemId: selectedId,
                        VersionId: resolved.VersionId,
                        LoadedVersionLabel: loadedVersionLabel,
                        ProjectName: "Active Project",
                        ProjectPath: projectPath,
                        RevitMajorVersion: targetRevit,
                        Action: "Load",
                        CreatedAtUtc: DateTimeOffset.UtcNow);

                    InvalidateLoadedFamilyNamesCache();

                    FireAndForget(async () =>
                    {
                        await _usageRepo.RecordUsageAsync(usage, CancellationToken.None);
                        await LoadTreeAsync();
                    });
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

    [RelayCommand(CanExecute = nameof(CanPlaceType))]
    private async Task PlaceTypeAsync()
    {
        if (SelectedTreeNode is not FamilyTypeNodeViewModel typeNode) return;

        var parent = FindParentOf(TreeNodes, typeNode);
        if (parent is not FamilyLeafNodeViewModel leaf) return;

        if (leaf.FamilySource == "system")
        {
            await PlaceSystemTypeAsync(leaf.CatalogItemId, typeNode.TypeName, CurrentRevitVersion);
            return;
        }

        var catalogItemId = leaf.CatalogItemId;
        var familyName = leaf.DisplayName;
        var typeName = typeNode.TypeName;
        var isVirtual = typeNode.IsVirtual;
        var targetRevit = CurrentRevitVersion;

        await _awaitableEvent.RaiseAsyncTask(async _ =>
        {
            try
            {
                var isFamilyLoaded = _familySearchService.IsFamilyLoaded(familyName);
                var isTypeLoaded = isFamilyLoaded && _familySearchService.HasFamilyType(familyName, typeName);

                if (!isFamilyLoaded || !isTypeLoaded)
                {
                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_Loading) ?? "Loading {0}...",
                        typeName);

                    var resolved = await _fileResolver
                        .ResolveForLoadAsync(catalogItemId, targetRevit, CancellationToken.None)
                        .ConfigureAwait(true);

                    if (string.IsNullOrEmpty(resolved.AbsolutePath))
                    {
                        StatusMessage = string.Format(
                            LanguageManager.GetString(StringLocalization.Keys.FM_FamilyFileNotFound) ?? "Family file not found",
                            familyName);
                        return;
                    }

                    FamilyLoadResult result;
                    if (isVirtual)
                    {
                        var options = FamilyLoadOptions.Default with { PreferredName = familyName };
                        result = await _loadService.LoadFamilyAsync(resolved, options, msg => StatusMessage = msg, CancellationToken.None).ConfigureAwait(true);
                    }
                    else
                    {
                        result = await _loadService.LoadFamilySymbolAsync(resolved.AbsolutePath, typeName, msg => StatusMessage = msg, CancellationToken.None).ConfigureAwait(true);
                    }

                    if (!result.Success)
                    {
                        StatusMessage = string.Format(
                            LanguageManager.GetString(StringLocalization.Keys.FM_LoadError) ?? "Load error: {0}",
                            result.ErrorMessage ?? $"Failed to load type '{typeName}'");
                        return;
                    }
                }

                var placementSuccess = _familyPlacementService.ActivateAndPlaceType(familyName, typeName);
                if (placementSuccess)
                {
                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_LoadAndPlaceSuccess) ?? "Family \"{0}\" — click to place",
                        familyName);

                    // Record usage analytics
                    var resolvedForUsage = await _fileResolver
                        .ResolveForLoadAsync(catalogItemId, targetRevit, CancellationToken.None)
                        .ConfigureAwait(true);

                    var projectPath = _revitContext.GetDocument().PathName;
                    var usage = new ProjectFamilyUsage(
                        Id: Guid.NewGuid().ToString(),
                        CatalogItemId: catalogItemId,
                        VersionId: resolvedForUsage.VersionId,
                        LoadedVersionLabel: resolvedForUsage.VersionLabel,
                        ProjectName: "Active Project",
                        ProjectPath: projectPath,
                        RevitMajorVersion: targetRevit,
                        Action: "Place",
                        CreatedAtUtc: DateTimeOffset.UtcNow);

                    FireAndForget(async () =>
                    {
                        try { await _usageRepo.RecordUsageAsync(usage, CancellationToken.None); }
                        catch { /* ignored */ }
                    });
                }
                else
                {
                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_LoadError) ?? "Load error: {0}",
                        $"Type '{typeName}' not found in family '{familyName}' after loading");
                }
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn($"PlaceType failed: {ex.Message}");
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_LoadError) ?? "Load error: {0}",
                    ex.Message);
            }
        });
    }

    private async Task PlaceSystemTypeAsync(string catalogItemId, string typeName, int targetRevit)
    {
        await _awaitableEvent.RaiseAsync(_ =>
        {
            try
            {
                _systemFamilyPlacementService.LoadAndPlaceSystemType(catalogItemId, typeName, targetRevit);
                StatusMessage = $"Системный тип \"{typeName}\" — click to place";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Load error: {ex.Message}";
            }
        });
    }
}