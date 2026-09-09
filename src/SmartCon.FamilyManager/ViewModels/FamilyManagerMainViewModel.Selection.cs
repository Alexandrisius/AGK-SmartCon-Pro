using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Common;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Events;
using SmartCon.FamilyManager.Selectors;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.Services.LocalCatalog;
using SmartCon.FamilyManager.Services.Stale;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyManagerMainViewModel
{
    partial void OnSearchTextChanged(string value)
    {
        var isSearchNow = !string.IsNullOrWhiteSpace(value);
        var savedCatCount = _savedExpandedCategoryIds.Count;
        var savedFamCount = _savedExpandedFamilyIds.Count;

        if (isSearchNow && !_lastSearchActive)
        {
            _savedExpandedCategoryIds.Clear();
            _savedExpandedFamilyIds.Clear();
            CollectExpandedIds(TreeNodes, _savedExpandedCategoryIds, _savedExpandedFamilyIds);
        }

        _lastSearchActive = isSearchNow;

        // DIAG-DUMP (Issue: net48 tree-expand after search).
        // Tracks the lifecycle of the search box so we can correlate the user
        // typing a term with the eventual TreeViewItem.IsExpanded state.
        // Without this, the search logic in OnSearchTextChanged → DebouncedSearchAsync
        // → LoadTreeAsync is invisible in the log.
        SmartConLogger.Info(
            $"FMTree.SearchTextChanged: newValue='{value}' isSearch={isSearchNow} " +
            $"prevSearchActive={!isSearchNow != _lastSearchActive} " +
            $"savedCats={savedCatCount} savedFams={savedFamCount} " +
            $"treeNodesBefore={TreeNodes.Count}");

        var newCts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _searchCts, newCts);
        oldCts?.Cancel();
        oldCts?.Dispose();
        _ = DebouncedSearchAsync(newCts.Token);
    }

    private async Task DebouncedSearchAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(300, ct);
            // DIAG-DUMP: search debounce elapsed, now triggering LoadTreeAsync
            SmartConLogger.Debug(
                $"FMTree.DebouncedSearch: 300ms elapsed, calling LoadTreeAsync. " +
                $"thread={Environment.CurrentManagedThreadId} syncCtx={SynchronizationContext.Current?.GetType().Name ?? "<none>"}");
            await LoadTreeAsync(ct);
        }
        catch (OperationCanceledException)
        {
            SmartConLogger.Debug(
                $"FMTree.DebouncedSearch: cancelled (newer keystroke took over)");
        }
    }

    partial void OnSelectedItemChanged(FamilyCatalogItemRow? value)
    {
        RefreshCanLoadToProject();
    }

    partial void OnSelectedTreeNodeChanged(CatalogTreeNodeViewModel? value)
    {
        if (value is FamilyLeafNodeViewModel leaf)
        {
            SelectedItem = new FamilyCatalogItemRow
            {
                Id = leaf.CatalogItemId,
                Name = leaf.DisplayName,
                CategoryId = leaf.CategoryId,
                CategoryName = leaf.CategoryPath,
                Manufacturer = leaf.Manufacturer,
                ContentStatus = leaf.ContentStatus,
                VersionLabel = leaf.VersionLabel,
                UpdatedAtUtc = leaf.UpdatedAtUtc,
                Tags = leaf.Tags,
                Description = leaf.Description,
                RevitCategory = leaf.RevitCategory,
                FamilySource = leaf.FamilySource,
                ActiveRevitMajorVersion = leaf.ActiveRevitMajorVersion,
                MinRevitMajorVersion = leaf.MinRevitMajorVersion,
            };
        }
        else if (value is FamilyTypeNodeViewModel typeNode)
        {
            var parent = FindParentOf(TreeNodes, typeNode);
            if (parent is FamilyLeafNodeViewModel parentLeaf)
            {
                SelectedItem = new FamilyCatalogItemRow
                {
                    Id = parentLeaf.CatalogItemId,
                    Name = parentLeaf.DisplayName,
                    CategoryId = parentLeaf.CategoryId,
                    CategoryName = parentLeaf.CategoryPath,
                    Manufacturer = parentLeaf.Manufacturer,
                    ContentStatus = parentLeaf.ContentStatus,
                    VersionLabel = parentLeaf.VersionLabel,
                    UpdatedAtUtc = parentLeaf.UpdatedAtUtc,
                    Tags = parentLeaf.Tags,
                    Description = parentLeaf.Description,
                    RevitCategory = parentLeaf.RevitCategory,
                    FamilySource = parentLeaf.FamilySource,
                    ActiveRevitMajorVersion = parentLeaf.ActiveRevitMajorVersion,
                    MinRevitMajorVersion = parentLeaf.MinRevitMajorVersion,
                };
            }
            else
            {
                SelectedItem = null;
            }
        }
        else
        {
            SelectedItem = null;
        }

        RefreshCanPlaceType();
        StartPlacementDragCommand.NotifyCanExecuteChanged();
        ImportFileToCategoryCommand.NotifyCanExecuteChanged();
    }

    private bool IsSelectedItemRevitIncompatible()
        => SelectedItem is not null
            && SelectedItem.ActiveRevitMajorVersion.HasValue
            && CurrentRevitVersion > 0
            && SelectedItem.ActiveRevitMajorVersion.Value > CurrentRevitVersion;

    private void RefreshCanLoadToProject()
    {
        CanLoadToProject = SelectedItem is not null
            && SelectedItem.ContentStatus == ContentStatus.Active
            && !IsSelectedItemRevitIncompatible()
            && _accessControl.CanLoadToProject
            && _activeBaseCompatibleWithCurrentDoc;
        LoadToProjectCommand.NotifyCanExecuteChanged();
        LoadToProjectKeepParamsCommand.NotifyCanExecuteChanged();
    }

    private void RefreshCanPlaceType()
    {
        if (SelectedTreeNode is FamilyTypeNodeViewModel typeNode)
        {
            var parent = FindParentOf(TreeNodes, typeNode);
            if (parent is FamilyLeafNodeViewModel parentLeaf)
            {
                // #187: menu/command agreement — "Разместить" is hidden for a
                // stale type (only "Обновить" is offered), so CanExecute must
                // agree.
                CanPlaceType = typeNode.PresenceState != TypePresenceState.StaleInProject
                    && CanLoadLeafToProject(parentLeaf);
            }
            else
            {
                CanPlaceType = false;
            }
        }
        else
        {
            CanPlaceType = false;
        }

        PlaceTypeCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void OnTreeViewSelectedItemChanged(object? selectedItem)
    {
        SelectedTreeNode = selectedItem as CatalogTreeNodeViewModel;
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = string.Empty;
    }
}
