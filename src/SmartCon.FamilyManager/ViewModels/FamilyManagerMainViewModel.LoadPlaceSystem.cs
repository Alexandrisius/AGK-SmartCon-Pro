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
                    CurrentRevitVersion,
                    confirmRoutingOverwrite: true);
            }
            catch (Exception ex)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_LoadError) ?? "Load error: {0}",
                    ex.Message);
            }
        }).ConfigureAwait(true);

        if (result is null) return;

        // ADR-072 World B (audit M8): the user declined the routing
        // overwrite confirmation — a quiet cancel, not an error.
        if (result.TypeResults.Count > 0
            && result.TypeResults.All(r => r.Status == SystemTypeSyncStatus.Cancelled))
        {
            StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_RoutingOverwriteCancelled)
                ?? "Load cancelled — the project routing will not be changed";
            return;
        }

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

        // Audit M6: custom parameters ported into the project are surfaced —
        // never a silent side effect (owner decision 2026-08-31).
        AppendPortedParametersMessage(result.TypeResults);

        if (result.AllSucceeded)
        {
            // Prune only on full success: a partially synchronized item still
            // has stale types and must keep its badge until the next Check.
            _staleDetector.MarkUpdated([leaf.CatalogItemId]);
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
                    CurrentRevitVersion,
                    confirmRoutingOverwrite: true);

                var typeResult = result.TypeResults.Count > 0 ? result.TypeResults[0] : null;
                // ADR-072 World B (audit M8): the user declined the routing
                // overwrite confirmation — a quiet cancel, not an error.
                if (typeResult?.Status == SystemTypeSyncStatus.Cancelled)
                {
                    StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_RoutingOverwriteCancelled)
                        ?? "Load cancelled — the project routing will not be changed";
                    return;
                }
                syncSucceeded = typeResult is not null && typeResult.IsSuccess;
                StatusMessage = syncSucceeded
                    ? string.Format(
                        LocalizationService.GetString("FM_SystemTypesSynced")
                            ?? "\"{0}\": synchronized {1} of {2} types",
                        typeNode.TypeName, result.SuccessCount, result.TypeResults.Count)
                    : string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_LoadError) ?? "Load error: {0}",
                        typeResult?.ErrorMessage ?? typeNode.TypeName);
                AppendPortedParametersMessage(result.TypeResults);

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

        return CanLoadLeafToProject(leaf);
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
        if (typeNode is null) return false;

        var parent = FindParentOf(TreeNodes, typeNode);
        if (parent is not FamilyLeafNodeViewModel leaf) return false;

        return CanUpdateTypeNode(
            typeNode.IsInProject,
            typeNode.IsStaleInProject,
            leaf.ContentStatus,
            leaf.IsRevitIncompatible,
            _accessControl.CanLoadToProject,
            _activeBaseCompatibleWithCurrentDoc);
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

    /// <summary>
    /// Audit M6: appends the names of custom parameter definitions the sync
    /// ported into the project (up to 5, then "+N") to
    /// <see cref="StatusMessage"/> — porting stays unconditional, but it is
    /// always visible (owner decision 2026-08-31).
    /// </summary>
    private void AppendPortedParametersMessage(IReadOnlyList<SystemTypeSyncResult> typeResults)
    {
        var portedNames = typeResults
            .Where(r => r.PortedParameterNames is not null)
            .SelectMany(r => r.PortedParameterNames!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (portedNames.Count == 0)
            return;
        var display = portedNames.Count <= 5
            ? string.Join(", ", portedNames)
            : string.Join(", ", portedNames.Take(5)) + $" (+{portedNames.Count - 5})";
        StatusMessage += string.Format(
            LanguageManager.GetString(StringLocalization.Keys.FM_ParametersPorted)
                ?? "; added parameters: {0}",
            display);
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
                    // ADR-072 World B: the user declined the routing
                    // overwrite — silent cancel, not an error.
                    SystemPlacementResult.Cancelled => string.Empty,
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

        if (result != SystemPlacementResult.Failed && result != SystemPlacementResult.Cancelled)
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

        // TryGetDocument (#219): zero-document state must be a quiet no-op,
        // not an NRE from the throwing GetDocument().
        var doc = _revitContext.TryGetDocument();
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
