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

        return leaf.ContentStatus == ContentStatus.Active && _accessControl.CanLoadToProject && _activeBaseCompatibleWithCurrentDoc;
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

    // ── Issue #101: dedicated Stale Update commands ───────────────────
    // "Обновить" (single leaf) was previously wired to LoadToProject*Command,
    // which calls Document.LoadFamily and pulls in EVERY type defined in the
    // .rfa — even if the user originally loaded only one type via
    // LoadFamilySymbol. These dedicated commands delegate to
    // IStaleFamilyUpdater.UpdateFamilyAsync, whose UpdateFamilyCoreAsync now
    // calls ReloadFamilyPreservingLoadedTypesAsync (per-type LoadFamilySymbol)
    // so only the already-loaded types are refreshed.

    [RelayCommand(CanExecute = nameof(CanLoadToProject))]
    private async Task UpdateStale()
    {
        await ExecuteUpdateStaleAsync(overwriteParameterValues: true);
    }

    [RelayCommand(CanExecute = nameof(CanLoadToProject))]
    private async Task UpdateStaleKeepParams()
    {
        await ExecuteUpdateStaleAsync(overwriteParameterValues: false);
    }

    private async Task ExecuteUpdateStaleAsync(bool overwriteParameterValues)
    {
        if (SelectedItem is null) return;
        if (!await EnsureDatabaseUpToDateForLoadAsync().ConfigureAwait(true)) return;

        var selectedId = SelectedItem.Id;
        var selectedName = SelectedItem.Name;
        var targetRevit = CurrentRevitVersion;

        await _awaitableEvent.RaiseAsyncTask(async _ =>
        {
            try
            {
                var success = await _staleUpdater.UpdateFamilyAsync(
                    selectedId, overwriteParameterValues, CancellationToken.None)
                    .ConfigureAwait(true);

                if (success)
                {
                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_LoadSuccess) ?? "Family \"{0}\" updated to latest version",
                        selectedName);

                    _staleDetector.MarkUpdated([selectedId]);
                    InvalidateLoadedFamilyNamesCache();
                    await LoadTreeAsync().ConfigureAwait(true);
                }
                else
                {
                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_LoadError) ?? "Update error: {0}",
                        selectedName);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_LoadError) ?? "Update error: {0}",
                    ex.Message);
            }
        });
    }

    private async Task ExecuteLoadOrUpdateAsync(bool overwriteParameterValues)
    {
        if (SelectedItem is null) return;
        if (!await EnsureDatabaseUpToDateForLoadAsync().ConfigureAwait(true)) return;

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
                var result = await _loadService.LoadFamilyAsync(
                    resolved,
                    loadOptions,
                    onStatusMessage: null,
                    onSharedDecision: request => _dialogService.ShowSharedFamiliesLoadModeDialog(request),
                    ct: CancellationToken.None).ConfigureAwait(true);

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

                    await _versionWriter.WriteVersionMarkerAsync(
                        selectedId,
                        loadedName,
                        resolved.VersionLabel,
                        targetRevit,
                        CancellationToken.None).ConfigureAwait(true);

                    // Drop only this family from the snapshot so the next Check
                    // re-evaluates it from scratch. Other categories' stale markers
                    // (and the families that were not updated) stay intact.
                    _staleDetector.MarkUpdated([selectedId]);
                    InvalidateLoadedFamilyNamesCache();
                    await LoadTreeAsync().ConfigureAwait(true);
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

        // Loadable placement loads the family into the project — gate it
        // on the database update state (Issue #126).
        if (!await EnsureDatabaseUpToDateForLoadAsync().ConfigureAwait(true)) return;

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
                    Func<SharedFamilyDecisionRequest, SharedFamiliesLoadChoice> sharedDecision =
                        request => _dialogService.ShowSharedFamiliesLoadModeDialog(request);

                    if (isVirtual)
                    {
                        var options = FamilyLoadOptions.Default with { PreferredName = familyName };
                        result = await _loadService.LoadFamilyAsync(
                            resolved,
                            options,
                            onStatusMessage: msg => StatusMessage = msg,
                            onSharedDecision: sharedDecision,
                            ct: CancellationToken.None).ConfigureAwait(true);
                    }
                    else
                    {
                        result = await _loadService.LoadFamilySymbolAsync(
                            resolved.AbsolutePath,
                            typeName,
                            onStatusMessage: msg => StatusMessage = msg,
                            onSharedDecision: sharedDecision,
                            catalogItemId: catalogItemId,
                            ct: CancellationToken.None).ConfigureAwait(true);
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

                    // Persist fresh ES marker for the loaded family (Phase 24).
                    var resolvedForMarker = await _fileResolver
                        .ResolveForLoadAsync(catalogItemId, targetRevit, CancellationToken.None)
                        .ConfigureAwait(true);

                    await _versionWriter.WriteVersionMarkerAsync(
                        catalogItemId,
                        familyName,
                        resolvedForMarker.VersionLabel,
                        targetRevit,
                        CancellationToken.None).ConfigureAwait(true);

                    // Drop only this family from the snapshot (same rationale
                    // as ExecuteLoadOrUpdateAsync above).
                    _staleDetector.MarkUpdated([catalogItemId]);
                    // Rebuild the tree so the leaf's IsStale flag drops and
                    // the category's HasStale / StaleCount roll-up updates.
                    // Without this, the leaf stays "stale" in the UI until
                    // the next Check or full tree reload.
                    await LoadTreeAsync().ConfigureAwait(true);
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
                using var _scope = SmartConLogger.BeginScope("PlaceType", ("CatalogItemId", catalogItemId));
                SmartConLogger.Warn(
                    $"PlaceType failed: {ex.Message}. [Action: report to user, retry from context menu]");
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
                StatusMessage = string.Format(
                    LocalizationService.GetString("FM_PlaceSystemType") ?? "System type \"{0}\" — click to place",
                    typeName);
            }
            catch (Exception ex)
            {
                StatusMessage = string.Format(
                    LocalizationService.GetString("FM_LoadError") ?? "Load error: {0}",
                    ex.Message);
            }
        });
    }
}