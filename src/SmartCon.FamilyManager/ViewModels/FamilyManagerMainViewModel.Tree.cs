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
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        var stageSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            stageSw.Restart();
            IReadOnlyList<Core.Models.FamilyManager.CategoryNode> categories = [];
            try
            {
                categories = await _categoryRepository.GetAllAsync(ct);
            }
            catch (Exception ex)
            {
                using var _scope = SmartConLogger.BeginScope("LoadTreeAsync", ("Stage", "GetAllAsync"));
                SmartConLogger.Warn($"failed: {ex.Message} [Action: нажмите Refresh чтобы перезагрузить дерево, проверьте БД каталога]");
            }
            SmartConLogger.Freeze($"LoadTreeAsync: GetAllAsync took {stageSw.ElapsedMilliseconds}ms, categories={categories.Count}");

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

            stageSw.Restart();
            var results = await _catalogProvider.SearchAsync(query, ct);
            SmartConLogger.Freeze($"LoadTreeAsync: SearchAsync took {stageSw.ElapsedMilliseconds}ms, results={results.Count}");

            stageSw.Restart();
            TotalItemCount = await _catalogProvider.GetItemCountAsync(ct);
            SmartConLogger.Freeze($"LoadTreeAsync: GetItemCountAsync took {stageSw.ElapsedMilliseconds}ms, totalItemCount={TotalItemCount}");

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

            // Phase 24: stale markers come from the detector snapshot (ADR-030).
            // The tree is initially fresh; users run Проверить to populate stale markers.
            var staleSnapshot = _staleDetector.GetCachedSnapshot();

            var itemsByCategory = results
                .GroupBy(i => i.CategoryId ?? string.Empty)
                .ToDictionary(g => g.Key, g => g.ToList());

            stageSw.Restart();
            var rootCategoryCount = 0;
            foreach (var catNode in tree.GetRootNodes())
            {
                var catVm = BuildCategoryNode(tree, catNode, itemsByCategory, expandAll, expandedIds, staleSnapshot);
                if (catVm is CategoryNodeViewModel)
                {
                    rootNodes.Add(catVm);
                    rootCategoryCount++;
                }
            }
            SmartConLogger.Freeze($"LoadTreeAsync: BuildCategoryNode took {stageSw.ElapsedMilliseconds}ms, rootCategories={rootCategoryCount}");

            var uncategorized = results.Where(r => string.IsNullOrEmpty(r.CategoryId)).ToList();
            var noCatLabel = LanguageManager.GetString(StringLocalization.Keys.FM_NoCategory) ?? "No category";
            _noCategoryNode = new CategoryNodeViewModel(
                categoryId: "__no_category__",
                name: noCatLabel,
                parentId: null,
                fullPath: noCatLabel);
            foreach (var item in uncategorized)
            {
                StaleCheckResult? staleResult = null;
                staleSnapshot?.Results.TryGetValue(item.Id, out staleResult);
                var isStale = staleResult?.IsStale ?? false;
                var staleReason = staleResult?.Reason ?? StaleReason.None;

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
                    FamilySource = item.FamilySource,
                }, isStale: isStale, staleReason: staleReason));
            }
            _noCategoryNode.FamilyCount = uncategorized.Count;
            if (!expandAll && expandedIds.Contains("__no_category__")) _noCategoryNode.IsExpanded = true;
            _noCategoryNode.AttachCollapseTracking();
            rootNodes.Add(_noCategoryNode);

            stageSw.Restart();
            try
            {
                await AttachCachedTypesAsync(rootNodes, expandedFamilyIds, ct);
            }
            catch (Exception ex)
            {
                using var _scope = SmartConLogger.BeginScope("LoadTreeAsync", ("Stage", "AttachCachedTypesAsync"));
                SmartConLogger.Warn($"failed: {ex.Message} [Action: нажмите Refresh чтобы перезагрузить дерево, проверьте БД каталога]");
            }
            SmartConLogger.Freeze($"LoadTreeAsync: AttachCachedTypesAsync took {stageSw.ElapsedMilliseconds}ms");

            stageSw.Restart();
            TreeNodes = rootNodes;
            SmartConLogger.Freeze($"LoadTreeAsync: TreeNodes= took {stageSw.ElapsedMilliseconds}ms (WPF binding sync)");

            // Re-apply per-category roll-up from the cached snapshot. BuildCategoryNode
            // only sets IsStale on leaves; the HasStale/StaleCount on category nodes
            // defaults to false. Without this call, every LoadTreeAsync (including
            // the one triggered by 'Update on a single family') would wipe the
            // HasStale indicator on every category — even ones whose stale markers
            // are still perfectly valid in the snapshot.
            stageSw.Restart();
            await ApplyStaleResultsToTreeAsync(Array.Empty<StaleCheckResult>(), ct).ConfigureAwait(true);
            SmartConLogger.Freeze($"LoadTreeAsync: ApplyStaleResultsToTreeAsync took {stageSw.ElapsedMilliseconds}ms");
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
            totalSw.Stop();
            SmartConLogger.Freeze($"LoadTreeAsync: TOTAL took {totalSw.ElapsedMilliseconds}ms, thread={Environment.CurrentManagedThreadId} treeNodes={TreeNodes.Count} treeRef={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(TreeNodes)}");
            SmartConLogger.Debug($"LoadTreeAsync: finally thread={Environment.CurrentManagedThreadId} treeNodes={TreeNodes.Count} treeRef={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(TreeNodes)}");
        }
    }

    private CatalogTreeNodeViewModel? BuildCategoryNode(
        CategoryTree tree,
        CategoryNode catNode,
        IReadOnlyDictionary<string, List<FamilyCatalogItem>> itemsByCategory,
        bool expandAll,
        HashSet<string>? expandedIds = null,
        FamilyStaleSnapshot? staleSnapshot = null)
    {
        var vm = new CategoryNodeViewModel(catNode);
        var familyCount = 0;

        foreach (var child in tree.GetChildren(catNode.Id))
        {
            var childVm = BuildCategoryNode(tree, child, itemsByCategory, expandAll, expandedIds, staleSnapshot);
            if (childVm is CategoryNodeViewModel childCat)
            {
                vm.Children.Add(childVm);
                familyCount += childCat.FamilyCount;
            }
        }

        if (itemsByCategory.TryGetValue(catNode.Id, out var items))
        {
            familyCount += items.Count;
            foreach (var item in items)
            {
                StaleCheckResult? staleResult = null;
                staleSnapshot?.Results.TryGetValue(item.Id, out staleResult);
                var isStale = staleResult?.IsStale ?? false;
                var staleReason = staleResult?.Reason ?? StaleReason.None;

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
                    FamilySource = item.FamilySource,
                }, isStale: isStale, staleReason: staleReason));
            }
        }

        vm.FamilyCount = familyCount;
        if (familyCount > 0)
        {
            if (expandAll) vm.IsExpanded = true;
            else if (expandedIds is not null && expandedIds.Contains(catNode.Id)) vm.IsExpanded = true;
        }
        vm.AttachCollapseTracking();
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
