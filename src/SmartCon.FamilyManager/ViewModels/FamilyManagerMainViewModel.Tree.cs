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

            // DIAG-DUMP (Issue: net48 tree-expand after search).
            // Logs the search-vs-restore decision BEFORE building root nodes so
            // we can correlate with the per-category IsExpanded outcome below.
            SmartConLogger.Info(
                $"FMTree.LoadTreeAsync.begin: searchText='{SearchText}' expandAll={expandAll} " +
                $"savedCatIds={_savedExpandedCategoryIds.Count} " +
                $"savedFamIds={_savedExpandedFamilyIds.Count}");

            var expandedIds = new HashSet<string>();
            var expandedFamilyIds = new HashSet<string>();
            if (!expandAll)
            {
                if (_savedExpandedCategoryIds.Count > 0)
                {
                    expandedIds = new HashSet<string>(_savedExpandedCategoryIds);
                    expandedFamilyIds = new HashSet<string>(_savedExpandedFamilyIds);
                }
                else
                {
                    // При DnD категоризации или любом другом перезагрузе без поиска
                    // saved пуст — берём текущее состояние из TreeNodes чтобы пользователь
                    // не терял раскрытые категории/семейства после LoadTreeAsync.
                    CollectExpandedIds(TreeNodes, expandedIds, expandedFamilyIds);
                }
            }
            else
            {
                // При активном поиске все категории с family items раскрываются принудительно
                // (expandAll=true в BuildCategoryNode), но семейства по умолчанию свёрнуты.
                // Здесь собираем только раскрытые семейства, чтобы placement DnD (который
                // вызывает LoadTreeAsync с тем же SearchText) не сбрасывал состояние пользователя.
                CollectExpandedIds(TreeNodes, [], expandedFamilyIds);
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
            // Issue #98 fix: previously this line only honoured the search-inactive
            // restore path (`!expandAll && expandedIds.Contains(...)`) — meaning
            // that during an active search (expandAll=true), the "Без категории"
            // node stayed at its default IsExpanded=false. The matching family
            // existed in the tree but was hidden under a collapsed parent.
            // Mirror the logic from BuildCategoryNode:279-293 so the _noCategoryNode
            // expands on search exactly like ordinary category nodes do.
            var noCatExpandedBefore = _noCategoryNode.IsExpanded;
            if (expandAll)
            {
                if (_noCategoryNode.FamilyCount > 0)
                    _noCategoryNode.IsExpanded = true;
            }
            else if (expandedIds.Contains("__no_category__"))
            {
                _noCategoryNode.IsExpanded = true;
            }
            SmartConLogger.Debug(
                $"FMTree.BuildNoCategory: familyCount={_noCategoryNode.FamilyCount} " +
                $"isExpandedSet={_noCategoryNode.IsExpanded != noCatExpandedBefore} " +
                $"expandAll={expandAll} finalIsExpanded={_noCategoryNode.IsExpanded}");
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

            // Удаляем пустые категории при активном поиске, чтобы пользователь видел
            // только ветки с совпадениями (best practice: скрывать нерелевантные разделы).
            // Вызывается ПОСЛЕ построения всего дерева (включая _noCategoryNode) и
            // AttachCachedTypesAsync, но ДО присвоения TreeNodes, чтобы PropertyChanged
            // коллекции не дёргал WPF лишний раз.
            StripEmptyCategories(rootNodes, expandAll);

            // При возврате из активного поиска принудительно сбрасываем визуальное
            // состояние TreeViewItem'ов через CollapseAll, затем восстанавливаем
            // сохранённые категории и семейства. WPF TreeView с TwoWay-биндингом IsExpanded
            // не сбрасывает визуальное состояние уже отрисованных TreeViewItem'ов при
            // переприсвоении ItemsSource, поэтому без явного CollapseAll категории,
            // развёрнутые через expandAll=true, остаются видимыми развёрнутыми даже после
            // возврата VM.IsExpanded=false.
            //
            // Ранее условие проверяло `_savedExpandedCategoryIds.Count > 0`, но при старте
            // поиска с полностью свёрнутым деревом saved пуст → условие ложно → категории
            // оставались развёрнутыми после очистки поиска. Теперь проверяем
            // `_previousLoadWasSearch` — был ли предыдущий LoadTreeAsync вызван поиском.
            // При DnD/Refresh без поиска _previousLoadWasSearch=false → CollapseAll
            // пропускается (BuildCategoryNode уже корректно выставил IsExpanded).
            if (!expandAll && _previousLoadWasSearch)
            {
                SmartConLogger.Info(
                    $"FMTree.LoadTreeAsync: returning from search — CollapseAll + Restore " +
                    $"(savedCatIds={_savedExpandedCategoryIds.Count}, savedFamIds={_savedExpandedFamilyIds.Count})");
                CollapseAll(rootNodes);
                RestoreExpandedState(rootNodes, _savedExpandedCategoryIds);
                RestoreExpandedFamilies(rootNodes, _savedExpandedFamilyIds);
                _savedExpandedCategoryIds.Clear();
                _savedExpandedFamilyIds.Clear();
            }
            else
            {
                SmartConLogger.Debug(
                    $"FMTree.LoadTreeAsync: no-collapse path taken " +
                    $"(expandAll={expandAll}, previousLoadWasSearch={_previousLoadWasSearch})");
            }

            _previousLoadWasSearch = expandAll;

            stageSw.Restart();
            TreeNodes = rootNodes;
            SmartConLogger.Freeze($"LoadTreeAsync: TreeNodes= took {stageSw.ElapsedMilliseconds}ms (WPF binding sync)");

            // Issue #98 defensive fix: WPF TreeView has documented quirks
            // (microsoft-ui-xaml #9549, #2112; reported on net48 specifically)
            // where the TwoWay IsExpanded binding can miss the initial source
            // value during container generation after an ItemsSource swap. Even
            // when the VM property is correctly set to true, the TreeViewItem
            // container may render collapsed. Re-raising PropertyChanged for any
            // already-expanded category forces the binding to re-evaluate after
            // the new containers exist. This is a no-op on platforms where the
            // binding already worked — it just emits an extra notification.
            //
            // We only re-notify ROOT categories here: child category containers
            // are realised later (when their parent expands), at which point the
            // binding reads the current VM value correctly without our help.
            foreach (var node in rootNodes)
            {
                if (node is CategoryNodeViewModel cat && cat.IsExpanded)
                {
                    cat.NotifyIsExpandedChanged();
                }
            }

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
        var isExpandedApplied = false;
        var isExpandedReason = "no-expand";
        if (familyCount > 0)
        {
            if (expandAll)
            {
                vm.IsExpanded = true;
                isExpandedApplied = true;
                isExpandedReason = "expandAll=true";
            }
            else if (expandedIds is not null && expandedIds.Contains(catNode.Id))
            {
                vm.IsExpanded = true;
                isExpandedApplied = true;
                isExpandedReason = $"in-expandedIds";
            }
        }
        // DIAG-DUMP (Issue: net48 tree-expand after search).
        // Logged at Debug to avoid log spam - 5 categories per rebuild is
        // acceptable but the path is critical for diagnosing why categories
        // do not visually expand on R2023 after search.
        SmartConLogger.Debug(
            $"FMTree.BuildCategoryNode: id='{catNode.Id}' name='{catNode.Name}' " +
            $"familyCount={familyCount} isExpandedSet={isExpandedApplied} reason={isExpandedReason} " +
            $"finalIsExpanded={vm.IsExpanded}");
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

    /// <summary>
    /// Удаляет категории без family items при активном поиске, чтобы пользователь
    /// видел только ветки, содержащие совпадения. При <paramref name="expandAll"/>=<c>false</c>
    /// (поиск неактивен) метод не трогает дерево.
    /// Категория считается пустой, если у неё <c>FamilyCount == 0</c> и ни одна дочерняя
    /// категория не содержит результатов. Идём по списку с конца, чтобы удаление было
    /// безопасным для итерации.
    /// </summary>
    internal static void StripEmptyCategories(ObservableCollection<CatalogTreeNodeViewModel> roots, bool expandAll)
    {
        if (!expandAll || roots is null)
        {
            return;
        }

        var beforeCount = CountAll(roots);
        StripEmptyRecursive(roots);
        var afterCount = CountAll(roots);
        // DIAG-DUMP (Issue: net48 tree-expand after search).
        // Confirms whether search actually pruned empty categories and how many
        // nodes were removed. If the user reports "no categories expanded", the
        // answer to "were there even matching categories?" lives here.
        SmartConLogger.Debug(
            $"FMTree.StripEmptyCategories: pruned {beforeCount - afterCount} empty category node(s) " +
            $"({beforeCount} → {afterCount})");
    }

    private static int CountAll(ObservableCollection<CatalogTreeNodeViewModel> nodes)
    {
        if (nodes is null) return 0;
        int count = nodes.Count;
        foreach (var node in nodes)
        {
            count += CountAll(node.Children);
        }
        return count;
    }

    private static void StripEmptyRecursive(ObservableCollection<CatalogTreeNodeViewModel> nodes)
    {
        for (int i = nodes.Count - 1; i >= 0; i--)
        {
            if (nodes[i] is not CategoryNodeViewModel cat)
            {
                continue;
            }

            StripEmptyRecursive(cat.Children);

            if (cat.FamilyCount == 0 && !HasNonEmptyCategoryDescendant(cat))
            {
                nodes.RemoveAt(i);
            }
        }
    }

    private static bool HasNonEmptyCategoryDescendant(CategoryNodeViewModel category)
    {
        foreach (var child in category.Children)
        {
            if (child is CategoryNodeViewModel childCat)
            {
                if (childCat.FamilyCount > 0 || HasNonEmptyCategoryDescendant(childCat))
                {
                    return true;
                }
            }
        }
        return false;
    }
}
