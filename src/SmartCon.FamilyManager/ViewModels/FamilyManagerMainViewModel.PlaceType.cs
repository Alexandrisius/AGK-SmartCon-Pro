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

        // #221: uniform for real and virtual (<default>) type nodes — a stale
        // type is refreshed through the family-scoped update path first, then
        // placed; fresh types place directly. Both gates are the leaf
        // load-gate (a stale presence already implies the family is in the
        // project, so no extra presence condition is needed here).
        return CanLoadLeafToProject(leaf);
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
}
