using System.IO;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.Services.Stale;
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
            typeNode.UniqueId,
            typeNode.FamilyName,
            typeNode.FamilyKey,
            // #210: freshness-on-place — a stale type is reloaded from the
            // catalog by the drop handler instead of placing the stale copy.
            typeNode.IsStaleInProject);

        // E2 (#209): a drifted parent must not reach the project via DnD —
        // block before the drag starts (the drop handler never runs).
        if (leaf.FamilySource != "system"
            && await CheckDependencyDriftBlockAsync(leaf.CatalogItemId, leaf.DisplayName).ConfigureAwait(true))
        {
            return;
        }

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

        // E2 (#209): hard block — the catalog content embeds outdated
        // nested families; loading it would plant them into the project.
        if (await CheckDependencyDriftBlockAsync(selectedId, selectedName).ConfigureAwait(true)) return;

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

        // A system type node selected (e.g. the leaf command invoked with a
        // type selected) — sync just that type, never the .rfa path.
        if (SelectedTreeNode is FamilyTypeNodeViewModel systemTypeNode && systemTypeNode.FamilySource == "system")
        {
            await LoadSystemTypeToProjectAsync(systemTypeNode).ConfigureAwait(true);
            return;
        }

        if (!await EnsureDatabaseUpToDateAsync().ConfigureAwait(true)) return;

        var selectedId = SelectedItem.Id;
        var selectedName = SelectedItem.Name;
        var targetRevit = CurrentRevitVersion;

        // E2 (#209): hard block on drifted dependencies (see PlaceTypeAsync).
        if (await CheckDependencyDriftBlockAsync(selectedId, selectedName).ConfigureAwait(true)) return;

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

                    // E2 (#209): the load planted the embedded nested copies
                    // into the project — mark them with their embedded
                    // version so they join the stale cycle.
                    await NestedDependencyMarkerWriter.WriteMarkersAsync(
                        _familyDependencyRepository,
                        _catalogProvider,
                        _versionWriter,
                        selectedId,
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

    /// <summary>
    /// #210: click on the presence dot — the third way to place a type
    /// (DnD, context menu, dot click). Freshness semantics per color:
    /// grey/blue place directly; orange (stale) refreshes the type through
    /// the standard update path FIRST, then places — users never place a
    /// stale copy from the dot.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPlaceTypeFromIndicator))]
    private async Task PlaceTypeFromIndicator(FamilyTypeNodeViewModel? typeNode)
    {
        if (typeNode is null) return;

        typeNode.IsSelected = true;
        if (typeNode.PresenceState == TypePresenceState.StaleInProject)
        {
            await UpdateTypeAsync(typeNode);
            // The successful loadable update rebuilds the whole tree — the
            // captured node is orphaned by it. Re-find the live node and
            // confirm the verdict flipped; a failed update must NEVER fall
            // through to placing the stale copy.
            var fresh = FindTypeNode(typeNode);
            if (fresh is null || fresh.PresenceState == TypePresenceState.StaleInProject)
            {
                return;
            }
            typeNode = fresh;
            typeNode.IsSelected = true;
        }

        var parent = FindParentOf(TreeNodes, typeNode);
        if (parent is not FamilyLeafNodeViewModel leaf) return;

        await PlaceTypeCoreAsync(typeNode, leaf);
    }

    /// <summary>Finds the live type node matching <paramref name="probe"/>
    /// (same catalog item + type name) in the CURRENT tree.</summary>
    private FamilyTypeNodeViewModel? FindTypeNode(FamilyTypeNodeViewModel probe) =>
        EnumerateAllLeaves(TreeNodes.OfType<CategoryNodeViewModel>())
            .Where(l => string.Equals(l.CatalogItemId, probe.CatalogItemId, StringComparison.Ordinal))
            .SelectMany(l => l.Children.OfType<FamilyTypeNodeViewModel>())
            .FirstOrDefault(t => string.Equals(t.TypeName, probe.TypeName, StringComparison.Ordinal));

    private bool CanPlaceTypeFromIndicator(FamilyTypeNodeViewModel? typeNode)
    {
        if (typeNode is null) return false;

        var parent = FindParentOf(TreeNodes, typeNode);
        if (parent is not FamilyLeafNodeViewModel leaf) return false;

        if (typeNode.PresenceState == TypePresenceState.StaleInProject)
        {
            // #221: typeless families (virtual <default> node) update
            // family-scoped — the per-type guard's IsVirtual exclusion does
            // not apply; the leaf-level update guard is the right one.
            return typeNode.IsVirtual ? CanUpdateStaleLeaf(leaf) : CanUpdateType(typeNode);
        }

        return leaf.ContentStatus == ContentStatus.Active
            && !leaf.IsRevitIncompatible
            && _accessControl.CanLoadToProject
            && _activeBaseCompatibleWithCurrentDoc;
    }

    [RelayCommand(CanExecute = nameof(CanPlaceType))]
    private async Task PlaceTypeAsync()
    {
        if (SelectedTreeNode is not FamilyTypeNodeViewModel typeNode) return;

        var parent = FindParentOf(TreeNodes, typeNode);
        if (parent is not FamilyLeafNodeViewModel leaf) return;

        await PlaceTypeCoreAsync(typeNode, leaf);
    }

    /// <summary>
    /// Selection-independent core of type placement (#210: the presence-dot
    /// shortcut calls it with the node re-found after an update-triggered
    /// tree rebuild — a selection would not survive it).
    /// </summary>
    private async Task PlaceTypeCoreAsync(FamilyTypeNodeViewModel typeNode, FamilyLeafNodeViewModel leaf)
    {
        if (leaf.FamilySource == "system")
        {
            await PlaceSystemTypeAsync(
                leaf.CatalogItemId, typeNode.TypeName, CurrentRevitVersion, typeNode.FamilyName, typeNode.FamilyKey);
            return;
        }

        // Loadable placement loads the family into the project — gate it
        // on the database update state (Issue #126).
        if (!await EnsureDatabaseUpToDateAsync().ConfigureAwait(true)) return;

        var catalogItemId = leaf.CatalogItemId;
        var familyName = leaf.DisplayName;

        // E2 (#209): hard block on drifted dependencies.
        if (await CheckDependencyDriftBlockAsync(catalogItemId, familyName).ConfigureAwait(true)) return;

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

                    // E2 (#209): nested dependency markers (see above).
                    await NestedDependencyMarkerWriter.WriteMarkersAsync(
                        _familyDependencyRepository,
                        _catalogProvider,
                        _versionWriter,
                        catalogItemId,
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
        // #183: full identity (family, name) per type — a bare name list
        // cannot distinguish "Стандарт" of two conduit families.
        var types = (await _typeRepository
                .GetTypesForItemAsync(leaf.CatalogItemId, CancellationToken.None)
                .ConfigureAwait(true))
            .Select(d => new SystemTypeRef(d.Name, d.FamilyName, d.FamilyKey))
            .ToList();

        if (types.Count == 0)
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
                    types,
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

        var syncSucceeded = false;
        await _awaitableEvent.RaiseAsync(_ =>
        {
            try
            {
                var result = _systemSyncOrchestrator.SyncTypes(
                    _revitContext.GetDocument(),
                    leaf.CatalogItemId,
                    new[] { new SystemTypeRef(typeNode.TypeName, typeNode.FamilyName, typeNode.FamilyKey) },
                    CurrentRevitVersion);

                var typeResult = result.TypeResults.Count > 0 ? result.TypeResults[0] : null;
                syncSucceeded = typeResult is not null && typeResult.IsSuccess;
                StatusMessage = syncSucceeded
                    ? string.Format(
                        LocalizationService.GetString("FM_SystemTypesSynced")
                            ?? "\"{0}\": synchronized {1} of {2} types",
                        typeNode.TypeName, result.SuccessCount, result.TypeResults.Count)
                    : string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_LoadError) ?? "Load error: {0}",
                        typeResult?.ErrorMessage ?? typeNode.TypeName);

                // #187 (review M1): the just-synced type carries a fresh ES
                // marker — clear its per-type stale verdict so the orange dot
                // drops immediately instead of waiting for a full "Проверить".
                if (syncSucceeded)
                {
                    _staleDetector.MarkSystemTypeUpdated(
                        leaf.CatalogItemId,
                        StaleDetector.BuildSystemTypeKey(typeNode.FamilyKey, typeNode.FamilyName, typeNode.TypeName));
                    typeNode.IsStaleInProject = false;
                    // The type was just synced INTO the project — the blue
                    // presence dot must appear without waiting for a tree
                    // rebuild (audit B4: it stayed grey until the next
                    // LoadTreeAsync).
                    typeNode.IsInProject = true;
                }
            }
            catch (Exception ex)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_LoadError) ?? "Load error: {0}",
                    ex.Message);
            }
        }).ConfigureAwait(true);

        // Manual test 2026-08-04: the per-type verdict cleared above is not
        // enough — the ITEM-level badge must be re-evaluated too. For a
        // single-type item (or when this was the last stale type) the family
        // is now current, and keeping the stale snapshot entry would show a
        // phantom badge until the next "Проверить". The check reads the
        // just-written ES marker and upserts the true verdict; for a
        // multi-type item with other stale types it correctly stays stale.
        if (syncSucceeded)
        {
            var systemResult = await _staleDetector.CheckSystemFamilyAsync(
                leaf.CatalogItemId, leaf.DisplayName, _revitContext.GetDocument(), CancellationToken.None)
                .ConfigureAwait(true);
            if (systemResult is not null)
            {
                leaf.IsStale = systemResult.IsStale;
                leaf.StaleReason = systemResult.Reason;
                await ApplyStaleResultsToTreeAsync([], CancellationToken.None)
                    .ConfigureAwait(true);
            }
        }
    }

    private bool CanLoadSystemTypeToProject(FamilyTypeNodeViewModel? typeNode)
    {
        // Same predicate as the menu visibility trigger (FamilySource ==
        // "system") — the command and the menu item must never disagree.
        if (typeNode is null || typeNode.FamilySource != "system") return false;

        var parent = FindParentOf(TreeNodes, typeNode);
        if (parent is not FamilyLeafNodeViewModel leaf) return false;

        return leaf.ContentStatus == ContentStatus.Active
            && !leaf.IsRevitIncompatible
            && _accessControl.CanLoadToProject
            && _activeBaseCompatibleWithCurrentDoc;
    }

    /// <summary>
    /// #187: uniform "Обновить" on a TYPE node — synchronizes the single type
    /// from the catalog reference. System types go through the ADR-061 sync
    /// (same as the former per-type "Загрузить в проект"); loadable types
    /// refresh their owning family while preserving the already-loaded types
    /// (#101) — per-symbol overwrite is not possible via the Revit API.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanUpdateType))]
    private async Task UpdateTypeAsync(FamilyTypeNodeViewModel? typeNode)
    {
        if (typeNode is null) return;

        var parent = FindParentOf(TreeNodes, typeNode);
        if (parent is not FamilyLeafNodeViewModel leaf) return;

        if (typeNode.FamilySource == "system")
        {
            await LoadSystemTypeToProjectAsync(typeNode);
            return;
        }

        // Loadable: the update is family-scoped (preserves loaded types) —
        // delegate to the stale-update path of the parent leaf. Side effect
        // (documented): the parent leaf becomes the selected node, matching
        // what the user right-clicked on.
        SelectedTreeNode = leaf;
        await ExecuteUpdateStaleAsync(overwriteParameterValues: true);
    }

    private bool CanUpdateType(FamilyTypeNodeViewModel? typeNode)
    {
        if (typeNode is null || typeNode.IsVirtual) return false;
        if (!typeNode.IsInProject && !typeNode.IsStaleInProject) return false;

        var parent = FindParentOf(TreeNodes, typeNode);
        if (parent is not FamilyLeafNodeViewModel leaf) return false;

        return leaf.ContentStatus == ContentStatus.Active
            && !leaf.IsRevitIncompatible
            && _accessControl.CanLoadToProject
            && _activeBaseCompatibleWithCurrentDoc;
    }

    /// <summary>
    /// E2 (#209, ADR-066): the load-time hard block for dependency drift.
    /// When the item's current version embeds a child version that is no
    /// longer the child's active version, loading the family would plant an
    /// outdated nested copy into the project — so the load is blocked with
    /// a styled dialog listing the drifted children. The heal path: open
    /// the family, drag the up-to-date nested versions from the catalog,
    /// re-import as a new version. Returns <c>true</c> when blocked.
    /// Pure SQL check (<see cref="IFamilyDependencyRepository.GetDependencyDriftBatchAsync"/>),
    /// no Revit-boundary work — safe to call from any load entry point.
    /// </summary>
    private async Task<bool> CheckDependencyDriftBlockAsync(string catalogItemId, string displayName)
    {
        IReadOnlyList<FamilyDependencyDrift> drifts;
        try
        {
            var batch = await _familyDependencyRepository
                .GetDependencyDriftBatchAsync(new[] { catalogItemId }, CancellationToken.None)
                .ConfigureAwait(true);
            if (!batch.TryGetValue(catalogItemId, out var list) || list.Count == 0)
            {
                return false;
            }
            drifts = list;
        }
        catch (Exception ex)
        {
            // Fail-OPEN: the drift check is a safety net, not a gate of
            // last resort — a DB hiccup must not break family loading.
            SmartConLogger.Warn(
                $"Dependency drift check failed for '{displayName}': {ex.Message} — load allowed " +
                "[Action: проверьте, что БД каталога доступна; amber-бейдж устаревших вложенных может быть неактуален]");
            return false;
        }

        var lines = string.Join("\n", drifts.Select(d => string.Format(
            LanguageManager.GetString(StringLocalization.Keys.FM_DependencyDriftBlock_Item)
                ?? "• {0} — зашита {1}, активна {2}",
            d.ChildName, d.EmbeddedVersionLabel, d.CurrentVersionLabel)));

        SmartConLogger.Warn(
            $"Load blocked by dependency drift: '{displayName}' embeds outdated nested families: " +
            $"{string.Join(", ", drifts.Select(d => $"{d.ChildName} ({d.EmbeddedVersionLabel}→{d.CurrentVersionLabel})"))}. " +
            "[Action: переимпортируйте родительское семейство с актуальными вложенными версиями]");

        _dialogService.ShowInfo(
            LanguageManager.GetString(StringLocalization.Keys.FM_DependencyDriftBlock_Title)
                ?? "Загрузка заблокирована",
            string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_DependencyDriftBlock_Body)
                    ?? "Семейство \"{0}\" содержит устаревшие вложенные семейства:\n{1}\n\nЗагрузка заблокирована, чтобы в проект не попали устаревшие копии. Откройте семейство, перетащите в него актуальные вложенные версии из каталога и переимпортируйте его с новой версией.",
                displayName, lines));

        StatusMessage = string.Format(
            LanguageManager.GetString(StringLocalization.Keys.FM_DependencyDriftBlock_Status)
                ?? "Загрузка \"{0}\" заблокирована: устаревшие вложенные семейства",
            displayName);
        return true;
    }

    private async Task PlaceSystemTypeAsync(
        string catalogItemId, string typeName, int targetRevit, string? familyName = null, string? familyKey = null)
    {
        var result = SystemPlacementResult.Failed;
        await _awaitableEvent.RaiseAsync(_ =>
        {
            try
            {
                result = _systemFamilyPlacementService.LoadAndPlaceSystemType(
                    catalogItemId, typeName, targetRevit, out var notConvergedCount,
                    familyName, familyKey);
                StatusMessage = result switch
                {
                    SystemPlacementResult.Placed => string.Format(
                        LocalizationService.GetString("FM_PlaceSystemType") ?? "System type \"{0}\" — click to place",
                        typeName),
                    SystemPlacementResult.LoadedManualPlacementRequired => string.Format(
                        LocalizationService.GetString("FM_PlaceSystemTypeManual")
                            ?? "Тип \"{0}\" загружен в проект. Разместите его вручную — например, изоляция применяется к существующей трубе или воздуховоду",
                        typeName),
                    _ => string.Format(
                        LocalizationService.GetString("FM_LoadError") ?? "Load error: {0}",
                        typeName),
                };
                if (result != SystemPlacementResult.Failed && notConvergedCount > 0)
                {
                    StatusMessage += string.Format(
                        LocalizationService.GetString("FM_SystemTypesNotConverged")
                            ?? "; not converged to reference: {0} (see the log)",
                        notConvergedCount);
                }
            }
            catch (Exception ex)
            {
                StatusMessage = string.Format(
                    LocalizationService.GetString("FM_LoadError") ?? "Load error: {0}",
                    ex.Message);
            }
        }).ConfigureAwait(true);

        if (result != SystemPlacementResult.Failed)
        {
            // Audit fix (was MarkUpdated): clear ONLY the synced type's
            // verdict and re-evaluate the item badge — sibling types of a
            // multi-type family keep their stale dots (#202 pattern).
            await ReevaluateSystemItemBadgeAsync(catalogItemId, typeName, familyName, familyKey)
                .ConfigureAwait(true);
            await RefreshSystemTypeProjectPresenceSafeAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Per-type badge maintenance after a single system type was synced
    /// (placement button, DnD): clears ONLY that type's stale verdict, then
    /// re-evaluates the item-level badge against the just-written ES marker.
    /// For a multi-type family with other stale types the badge correctly
    /// stays; for a fully current item it clears — without touching the
    /// per-type verdicts of the sibling types.
    /// </summary>
    internal async Task ReevaluateSystemItemBadgeAsync(
        string catalogItemId, string typeName, string? familyName, string? familyKey)
    {
        _staleDetector.MarkSystemTypeUpdated(
            catalogItemId, StaleDetector.BuildSystemTypeKey(familyKey, familyName, typeName));

        var doc = _revitContext.GetDocument();
        if (doc is null) return;

        var displayName = EnumerateAllLeaves(TreeNodes.OfType<CategoryNodeViewModel>())
            .FirstOrDefault(l => l.CatalogItemId == catalogItemId)?.DisplayName ?? typeName;
        var systemResult = await _staleDetector.CheckSystemFamilyAsync(
            catalogItemId, displayName, doc, CancellationToken.None).ConfigureAwait(true);
        if (systemResult is not null)
        {
            await ApplyStaleResultsToTreeAsync([], CancellationToken.None).ConfigureAwait(true);
        }
    }
}