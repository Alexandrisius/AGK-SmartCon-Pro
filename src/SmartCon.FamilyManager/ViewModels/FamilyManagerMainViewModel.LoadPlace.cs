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

        return CanLoadLeafToProject(leaf);
    }

    /// <summary>
    /// The single load-gate predicate for a leaf: every place/update command
    /// that loads catalog content into the project ANDs the same four
    /// conditions — active content, Revit-compatible, role allows loading,
    /// project base matches the document. Pure static form keeps it
    /// unit-testable (#221 follow-up: the dot and the context menu must never
    /// disagree — they now share this one predicate).
    /// </summary>
    internal static bool CanLoadLeafToProject(
        ContentStatus leafStatus,
        bool isRevitIncompatible,
        bool canLoadToProject,
        bool activeBaseCompatibleWithCurrentDoc) =>
        leafStatus == ContentStatus.Active
        && !isRevitIncompatible
        && canLoadToProject
        && activeBaseCompatibleWithCurrentDoc;

    private bool CanLoadLeafToProject(FamilyLeafNodeViewModel leaf) =>
        CanLoadLeafToProject(
            leaf.ContentStatus,
            leaf.IsRevitIncompatible,
            _accessControl.CanLoadToProject,
            _activeBaseCompatibleWithCurrentDoc);

    /// <summary>
    /// Pure form of the «Обновить» gate for a type node (#221 follow-up).
    /// Deliberately takes no <c>IsVirtual</c>: for a typeless family the
    /// update is family-scoped (<see cref="UpdateTypeAsync"/> delegates to the
    /// leaf's stale-update path, which needs no concrete type), so virtual
    /// <c>&lt;default&gt;</c> nodes update exactly like real ones.
    /// </summary>
    internal static bool CanUpdateTypeNode(
        bool isInProject,
        bool isStaleInProject,
        ContentStatus leafStatus,
        bool isRevitIncompatible,
        bool canLoadToProject,
        bool activeBaseCompatibleWithCurrentDoc) =>
        (isInProject || isStaleInProject)
        && CanLoadLeafToProject(leafStatus, isRevitIncompatible, canLoadToProject, activeBaseCompatibleWithCurrentDoc);

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

        // #222: the version the project currently HAS (stale snapshot / ES
        // marker) — the update report diffs per-type values against the
        // target version so «успешно» can say WHICH types actually changed.
        var fromVersionLabel =
            _staleDetector.GetCachedSnapshot() is { } snapshot
            && snapshot.Results.TryGetValue(selectedId, out var snapshotEntry)
                ? snapshotEntry.LoadedVersionLabel
                : null;

        // E2 (#209): hard block — the catalog content embeds outdated
        // nested families; loading it would plant them into the project.
        if (await CheckDependencyDriftBlockAsync(selectedId, selectedName).ConfigureAwait(true)) return;

        // #249 (Phase 2): pre-update per-type report — name the drifted
        // types whose local edits the catalog content will replace.
        if (!ConfirmDriftedTypesReplacement(selectedId)) return;

        await _awaitableEvent.RaiseAsyncTask(async _ =>
        {
            try
            {
                var updateResult = await _staleUpdater.UpdateFamilyAsync(
                        selectedId, overwriteParameterValues, fromVersionLabel, CancellationToken.None)
                    .ConfigureAwait(true);

                if (updateResult.Success)
                {
                    StatusMessage = BuildUpdateSuccessMessage(selectedName, updateResult);

                    _staleDetector.MarkUpdated([selectedId]);
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

    /// <summary>
    /// #249 (Phase 2): pre-update confirmation naming the DRIFTED types
    /// whose local edits the catalog content will replace. Fires ONLY for
    /// <see cref="StaleReason.ContentDrift"/> (manual-test round 5): a
    /// VersionMismatch family is simply OLDER — its types are not local
    /// edits, and an update is the expected action with no data-loss
    /// warning needed. Silent (true) when no per-type proof exists (the
    /// pre-#249 behaviour) or when every compared type matches the catalog.
    /// </summary>
    private bool ConfirmDriftedTypesReplacement(string catalogItemId)
    {
        // ADR-072 World B (audit L11): a routing-drift update also destroys
        // user configuration (live routing is replaced by the catalog
        // links) — confirm it exactly like a content drift.
        if (IsRoutingDrift(catalogItemId))
        {
            var systemMap = _staleDetector.GetSystemTypeStaleMap(catalogItemId);
            var driftedTypes = systemMap is null
                ? []
                : systemMap.Where(kv => kv.Value).Select(kv => kv.Key)
                    .OrderBy(n => n, StringComparer.Ordinal).ToList();
            var routingTitle = LanguageManager.GetString(StringLocalization.Keys.FM_UpdateReplaceTypesTitle)
                ?? "Family update";
            var routingMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_UpdateRoutingDriftConfirm)
                    ?? "The project routing differs from the catalog: {0}.\n\nThe update will replace the routing settings with the catalog ones.\n\nContinue?",
                driftedTypes.Count > 0 ? string.Join(", ", driftedTypes) : "—");
            return _dialogService.ShowConfirmation(routingTitle, routingMessage);
        }

        if (!IsContentDrift(catalogItemId))
        {
            return true;
        }

        var map = _staleDetector.GetLoadableTypeStaleMap(catalogItemId);
        if (map is null)
        {
            return true;
        }

        var drifted = map
            .Where(kv => kv.Value)
            .Select(kv => kv.Key)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        if (drifted.Count == 0)
        {
            return true;
        }

        var title = LanguageManager.GetString(StringLocalization.Keys.FM_UpdateReplaceTypesTitle)
            ?? "Family update";
        var message = string.Format(
            LanguageManager.GetString(StringLocalization.Keys.FM_UpdateReplaceTypesConfirm)
                ?? "Types modified in the project: {0}.\n\nThe update will replace your local edits of these types with the catalog content (the other types: {1} — match the catalog).\n\nContinue?",
            string.Join(", ", drifted),
            map.Count - drifted.Count);
        return _dialogService.ShowConfirmation(title, message);
    }

    /// <summary>The item's current snapshot verdict is a LOCAL content
    /// drift (the user edited the embedded copy after loading) — the only
    /// case where an update destroys user work and deserves a warning.</summary>
    private bool IsContentDrift(string catalogItemId)
        => _staleDetector.GetCachedSnapshot()?.Results.TryGetValue(catalogItemId, out var result) == true
            && result.Reason == StaleReason.ContentDrift;

    /// <summary>The item's current snapshot verdict is a ROUTING drift
    /// (ADR-072 World B) — an update replaces the live routing with the
    /// catalog links.</summary>
    private bool IsRoutingDrift(string catalogItemId)
        => _staleDetector.GetCachedSnapshot()?.Results.TryGetValue(catalogItemId, out var result) == true
            && result.Reason == StaleReason.RoutingDrift;

    /// <summary>
    /// #222: success message of a single stale update. When the content was
    /// already current (no reload happened) the message says so honestly;
    /// when the per-type diff is available it names the CHANGED types — and
    /// distinguishes types loaded in the project from not-loaded ones, so
    /// «успешно» never reads as «мой параметр обновился» for a change that
    /// landed in a type the project does not have.
    /// </summary>
    private static string BuildUpdateSuccessMessage(string selectedName, StaleFamilyUpdateResult result)
    {
        if (result.ContentAlreadyCurrent)
        {
            return string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_UpdateAlreadyCurrent)
                    ?? "Family \"{0}\" is already up-to-date — content matches the current catalog version",
                selectedName);
        }

        var message = string.Format(
            LanguageManager.GetString(StringLocalization.Keys.FM_LoadSuccess) ?? "Family \"{0}\" updated to latest version",
            selectedName);
        if (!result.TypeDiffAvailable)
        {
            return message;
        }

        if (result.ChangedLoadedTypeNames.Count > 0)
        {
            message += string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_UpdateChangedTypes)
                    ?? "; changed types: {0}",
                string.Join(", ", result.ChangedLoadedTypeNames));
        }
        if (result.ChangedNotLoadedTypeNames.Count > 0)
        {
            message += string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_UpdateChangedTypesNotLoaded)
                    ?? "; changes in not-loaded types: {0}",
                string.Join(", ", result.ChangedNotLoadedTypeNames));
        }
        if (result.ChangedLoadedTypeNames.Count == 0 && result.ChangedNotLoadedTypeNames.Count == 0)
        {
            message += LanguageManager.GetString(StringLocalization.Keys.FM_UpdateNoTypeChanges)
                ?? "; type values unchanged";
        }
        return message;
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

}