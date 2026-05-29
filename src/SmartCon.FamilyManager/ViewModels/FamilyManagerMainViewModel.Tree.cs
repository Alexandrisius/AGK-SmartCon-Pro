using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyManagerMainViewModel
{
    [RelayCommand]
    private async Task LoadTreeAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        try
        {
            IReadOnlyList<Core.Models.FamilyManager.CategoryNode> categories = [];
            try
            {
                categories = await _categoryRepository.GetAllAsync(ct);
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn($"LoadTreeAsync GetAllAsync failed: {ex.Message}");
            }

            var tree = new CategoryTree(categories);

            var query = new FamilyCatalogQuery(
                SearchText: string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
                CategoryFilter: null,
                StatusFilter: null,
                Tags: null,
                ManufacturerFilter: null,
                Sort: FamilyCatalogSort.NameAsc,
                Offset: 0,
                Limit: int.MaxValue);

            var results = await _catalogProvider.SearchAsync(query, ct);
            TotalItemCount = await _catalogProvider.GetItemCountAsync(ct);

            var rootNodes = new ObservableCollection<CatalogTreeNodeViewModel>();
            var expandAll = !string.IsNullOrWhiteSpace(SearchText);

            var expandedIds = new HashSet<string>();
            var expandedFamilyIds = new HashSet<string>();
            if (!expandAll)
            {
                if (_savedExpandedCategoryIds.Count > 0)
                {
                    expandedIds = new HashSet<string>(_savedExpandedCategoryIds);
                    expandedFamilyIds = new HashSet<string>(_savedExpandedFamilyIds);
                    _savedExpandedCategoryIds.Clear();
                    _savedExpandedFamilyIds.Clear();
                }
                else
                {
                    CollectExpandedIds(TreeNodes, expandedIds, expandedFamilyIds);
                }
            }

            // Stale marker: get loaded version labels for current project
            Dictionary<string, string?> loadedVersionLabels = new();
            try
            {
                var projectPath = _revitContext.GetDocument().PathName;
                SmartConLogger.Info($"[StaleDebug] ProjectPath: {projectPath}");
                if (!string.IsNullOrEmpty(projectPath))
                {
                    var familyIds = results.Select(r => r.Id).ToList();
                    loadedVersionLabels = (Dictionary<string, string?>)
                        await _usageRepo.GetLoadedVersionLabelsAsync(projectPath, familyIds, ct);
                    SmartConLogger.Info($"[StaleDebug] Loaded {loadedVersionLabels.Count} version labels for {familyIds.Count} families");
                    foreach (var kvp in loadedVersionLabels)
                    {
                        SmartConLogger.Info($"[StaleDebug]   Family {kvp.Key} -> loaded version: {kvp.Value ?? "null"}");
                    }
                }
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn($"LoadTreeAsync GetLoadedVersionLabelsAsync failed: {ex.Message}");
            }

            var itemsByCategory = results
                .GroupBy(i => i.CategoryId ?? string.Empty)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var catNode in tree.GetRootNodes())
            {
                var catVm = BuildCategoryNode(tree, catNode, itemsByCategory, expandAll, expandedIds, loadedVersionLabels);
                if (catVm is CategoryNodeViewModel) rootNodes.Add(catVm);
            }

            var uncategorized = results.Where(r => string.IsNullOrEmpty(r.CategoryId)).ToList();
            var noCatLabel = LanguageManager.GetString(StringLocalization.Keys.FM_NoCategory) ?? "No category";
            _noCategoryNode = new CategoryNodeViewModel(
                categoryId: "__no_category__",
                name: noCatLabel,
                parentId: null,
                fullPath: noCatLabel);
            int uncategorizedLogCount = 0;
            foreach (var item in uncategorized)
            {
                bool isStale = false;
                string? loadedVersionLabel = null;
                if (loadedVersionLabels is not null &&
                    loadedVersionLabels.TryGetValue(item.Id, out loadedVersionLabel) &&
                    loadedVersionLabel is not null &&
                    loadedVersionLabel != item.CurrentVersionLabel)
                {
                    isStale = true;
                }

                if (uncategorizedLogCount < 3)
                {
                    SmartConLogger.Info($"[StaleDebug] Uncategorized '{item.Name}' (ID:{item.Id}) - CurrentVersion: {item.CurrentVersionLabel ?? "null"}, LoadedVersion: {loadedVersionLabel ?? "null"}, IsStale: {isStale}");
                    uncategorizedLogCount++;
                }

                _noCategoryNode.Children.Add(new FamilyLeafNodeViewModel(new FamilyCatalogItemRow
                {
                    Id = item.Id,
                    Name = item.Name,
                    CategoryId = item.CategoryId,
                    CategoryName = noCatLabel,
                    Manufacturer = item.Manufacturer,
                    ContentStatus = item.ContentStatus,
                    CurrentVersionLabel = item.CurrentVersionLabel,
                    VersionLabel = item.CurrentVersionLabel,
                    UpdatedAtUtc = item.UpdatedAtUtc,
                    Tags = item.Tags,
                    Description = item.Description,
                }, isStale: isStale));
            }
            _noCategoryNode.FamilyCount = uncategorized.Count;
            if (!expandAll && expandedIds.Contains("__no_category__")) _noCategoryNode.IsExpanded = true;
            rootNodes.Add(_noCategoryNode);

            try
            {
                await AttachCachedTypesAsync(rootNodes, expandedFamilyIds, ct);
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn($"AttachCachedTypesAsync failed: {ex.Message}");
            }

            TreeNodes = rootNodes;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ImportError) ?? "Error: {0}",
                ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private CatalogTreeNodeViewModel? BuildCategoryNode(CategoryTree tree, CategoryNode catNode, IReadOnlyDictionary<string, List<FamilyCatalogItem>> itemsByCategory, bool expandAll, HashSet<string>? expandedIds = null, IReadOnlyDictionary<string, string?>? loadedVersionLabels = null)
    {
        var vm = new CategoryNodeViewModel(catNode);
        var familyCount = 0;

        foreach (var child in tree.GetChildren(catNode.Id))
        {
            var childVm = BuildCategoryNode(tree, child, itemsByCategory, expandAll, expandedIds, loadedVersionLabels);
            if (childVm is CategoryNodeViewModel childCat)
            {
                vm.Children.Add(childVm);
                familyCount += childCat.FamilyCount;
            }
        }

        if (itemsByCategory.TryGetValue(catNode.Id, out var items))
        {
            familyCount += items.Count;
            int logCount = 0;
                foreach (var item in items)
                {
                    bool isStale = false;
                    string? loadedVersionLabel = null;
                    if (loadedVersionLabels is not null &&
                        loadedVersionLabels.TryGetValue(item.Id, out loadedVersionLabel) &&
                        loadedVersionLabel is not null &&
                        loadedVersionLabel != item.CurrentVersionLabel)
                    {
                        isStale = true;
                    }

                    if (logCount < 3)
                    {
                        SmartConLogger.Info($"[StaleDebug] Family '{item.Name}' (ID:{item.Id}) - CurrentVersion: {item.CurrentVersionLabel ?? "null"}, LoadedVersion: {loadedVersionLabel ?? "null"}, IsStale: {isStale}");
                        logCount++;
                    }

                    vm.Children.Add(new FamilyLeafNodeViewModel(new FamilyCatalogItemRow
                    {
                        Id = item.Id,
                        Name = item.Name,
                        CategoryId = item.CategoryId,
                        CategoryName = catNode.FullPath,
                        Manufacturer = item.Manufacturer,
                        ContentStatus = item.ContentStatus,
                        CurrentVersionLabel = item.CurrentVersionLabel,
                        VersionLabel = item.CurrentVersionLabel,
                        UpdatedAtUtc = item.UpdatedAtUtc,
                        Tags = item.Tags,
                        Description = item.Description,
                    }, isStale: isStale));
                }
        }

        vm.FamilyCount = familyCount;
        if (familyCount > 0)
        {
            if (expandAll) vm.IsExpanded = true;
            else if (expandedIds is not null && expandedIds.Contains(catNode.Id)) vm.IsExpanded = true;
        }
        return vm;
    }

    private async Task AttachCachedTypesAsync(ObservableCollection<CatalogTreeNodeViewModel> rootNodes, HashSet<string> expandedFamilyIds, CancellationToken ct)
    {
        var familyIds = new List<string>();
        CollectFamilyIds(rootNodes, familyIds);
        if (familyIds.Count == 0) return;

        var batch = await _typeRepository.GetAllTypesBatchAsync(familyIds, ct);

        AttachTypesToNodes(rootNodes, batch, expandedFamilyIds);
    }
}
