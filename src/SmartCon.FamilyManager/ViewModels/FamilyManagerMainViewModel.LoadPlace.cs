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
    private async Task StartPlacementDrag(object? item)
    {
        if (item is not FamilyTypeNodeViewModel typeNode) return;

        var parent = FindParentOf(TreeNodes, typeNode);
        if (parent is not FamilyLeafNodeViewModel leaf) return;

        // DnD placement loads the family into the project via the Revit-side
        // drop handler — gate it here, before the drag starts (Issue #126).
        // System types only copy from the isolated .rvt (no catalog write).
        if (leaf.FamilySource != "system"
            && !await EnsureDatabaseUpToDateAsync().ConfigureAwait(true))
        {
            return;
        }

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

        return leaf.ContentStatus == ContentStatus.Active
            && !leaf.IsRevitIncompatible
            && _accessControl.CanLoadToProject
            && _activeBaseCompatibleWithCurrentDoc;
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
        if (!await EnsureDatabaseUpToDateAsync().ConfigureAwait(true)) return;

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

        // Issue #104: system families are synchronized from the mini-project
        // (no .rfa load). The gate on pending DB updates is not applied —
        // parity with system placement, which only reads the catalog.
        if (SelectedTreeNode is FamilyLeafNodeViewModel systemLeaf && systemLeaf.FamilySource == "system")
        {
            await ExecuteLoadSystemFamilyAsync(systemLeaf).ConfigureAwait(true);
            return;
        }

        if (!await EnsureDatabaseUpToDateAsync().ConfigureAwait(true)) return;

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
        if (!await EnsureDatabaseUpToDateAsync().ConfigureAwait(true)) return;

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

    /// <summary>
    /// "Загрузить в проект" for a system catalog item (Issue #104):
    /// synchronizes ALL types of the mini-project into the project without
    /// starting placement. One source-document open per call, one
    /// transaction per type.
    /// </summary>
    private async Task ExecuteLoadSystemFamilyAsync(FamilyLeafNodeViewModel leaf)
    {
        var typeNames = (await _typeRepository
                .GetTypesForItemAsync(leaf.CatalogItemId, CancellationToken.None)
                .ConfigureAwait(true))
            .Select(d => d.Name)
            .ToList();

        if (typeNames.Count == 0)
        {
            StatusMessage = string.Format(
                LocalizationService.GetString("FM_SystemTypeNoTypes")
                    ?? "\"{0}\": no types in the catalog — reimport the mini-project",
                leaf.DisplayName);
            return;
        }

        SystemFamilySyncResult? result = null;
        await _awaitableEvent.RaiseAsync(_ =>
        {
            try
            {
                result = _systemSyncOrchestrator.SyncTypes(
                    _revitContext.GetDocument(),
                    leaf.CatalogItemId,
                    typeNames,
                    CurrentRevitVersion);
            }
            catch (Exception ex)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_LoadError) ?? "Load error: {0}",
                    ex.Message);
            }
        }).ConfigureAwait(true);

        if (result is null) return;

        StatusMessage = result.FailedCount == 0
            ? string.Format(
                LocalizationService.GetString("FM_SystemTypesSynced")
                    ?? "\"{0}\": synchronized {1} of {2} types",
                leaf.DisplayName, result.SuccessCount, result.TypeResults.Count)
            : string.Format(
                LocalizationService.GetString("FM_SystemTypesSyncedWithErrors")
                    ?? "\"{0}\": synchronized {1} of {2} types, errors: {3}",
                leaf.DisplayName, result.SuccessCount, result.TypeResults.Count, result.FailedCount);

        if (result.TotalNotConverged > 0)
        {
            StatusMessage += string.Format(
                LocalizationService.GetString("FM_SystemTypesNotConverged")
                    ?? "; not converged to reference: {0} (see the log)",
                result.TotalNotConverged);
        }

        if (result.AllSucceeded)
        {
            // Prune only on full success: a partially synchronized item still
            // has stale types and must keep its badge until the next Check.
            _staleDetector.MarkUpdated([leaf.CatalogItemId]);
            InvalidateLoadedFamilyNamesCache();
            await LoadTreeAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanLoadSystemTypeToProject))]
    private async Task LoadSystemTypeToProjectAsync(FamilyTypeNodeViewModel? typeNode)
    {
        if (typeNode is null) return;

        var parent = FindParentOf(TreeNodes, typeNode);
        if (parent is not FamilyLeafNodeViewModel leaf) return;

        await _awaitableEvent.RaiseAsync(_ =>
        {
            try
            {
                var result = _systemSyncOrchestrator.SyncTypes(
                    _revitContext.GetDocument(),
                    leaf.CatalogItemId,
                    new[] { typeNode.TypeName },
                    CurrentRevitVersion);

                var typeResult = result.TypeResults.Count > 0 ? result.TypeResults[0] : null;
                StatusMessage = typeResult is not null && typeResult.IsSuccess
                    ? string.Format(
                        LocalizationService.GetString("FM_SystemTypesSynced")
                            ?? "\"{0}\": synchronized {1} of {2} types",
                        typeNode.TypeName, result.SuccessCount, result.TypeResults.Count)
                    : string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_LoadError) ?? "Load error: {0}",
                        typeResult?.ErrorMessage ?? typeNode.TypeName);
            }
            catch (Exception ex)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_LoadError) ?? "Load error: {0}",
                    ex.Message);
            }
        }).ConfigureAwait(true);

        // The stale snapshot is intentionally NOT pruned here: a single-type
        // load leaves the item's other types untouched, so the item-level
        // verdict can only be re-evaluated by the next "Проверить".
    }

    private bool CanLoadSystemTypeToProject(FamilyTypeNodeViewModel? typeNode)
    {
        if (typeNode is null || !typeNode.IsSystemType) return false;

        var parent = FindParentOf(TreeNodes, typeNode);
        if (parent is not FamilyLeafNodeViewModel leaf) return false;

        return leaf.ContentStatus == ContentStatus.Active
            && !leaf.IsRevitIncompatible
            && _accessControl.CanLoadToProject
            && _activeBaseCompatibleWithCurrentDoc;
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