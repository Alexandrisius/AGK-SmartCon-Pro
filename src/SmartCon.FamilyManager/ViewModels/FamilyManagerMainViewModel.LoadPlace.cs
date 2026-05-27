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
                    var loadedName = result.FamilyName ?? selectedName;
                    var isLoaded = _familySearchService.IsFamilyLoaded(loadedName);
                    CanPlace = isLoaded;
                    PlaceCommand.NotifyCanExecuteChanged();

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

    [RelayCommand(CanExecute = nameof(CanPlace))]
    private void Place()
    {
        if (SelectedItem is null) return;

        var familyName = SelectedItem.Name;

        _externalEvent.Raise(() =>
        {
            try
            {
                if (!_familySearchService.IsFamilyLoaded(familyName))
                {
                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_FamilyNotLoaded) ?? "Family \"{0}\" not loaded in project. Use 'Load to Project'.",
                        familyName);
                    CanPlace = false;
                    PlaceCommand.NotifyCanExecuteChanged();
                    return;
                }

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
                        LanguageManager.GetString(StringLocalization.Keys.FM_FamilyNotFoundAfterLoad) ?? "No types found");
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

    [RelayCommand]
    private void PlaceType()
    {
        if (SelectedTreeNode is not FamilyTypeNodeViewModel typeNode) return;

        var parent = FindParentOf(TreeNodes, typeNode);
        if (parent is not FamilyLeafNodeViewModel leaf) return;

        var familyName = leaf.DisplayName;
        var typeName = typeNode.TypeName;

        _externalEvent.Raise(() =>
        {
            try
            {
                _familyPlacementService.ActivateAndPlaceType(familyName, typeName);
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn($"PlaceType failed: {ex.Message}");
            }
        });
    }
}