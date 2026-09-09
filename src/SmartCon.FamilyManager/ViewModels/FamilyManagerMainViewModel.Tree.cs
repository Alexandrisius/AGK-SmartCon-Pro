using System.Collections.ObjectModel;
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
                Sort: FamilyCatalogSort.NameAsc,
                Offset: 0,
                Limit: int.MaxValue,
                // Import Validation Gate: the "Без категории" quarantine
                // zone is hidden from read-only roles (Engineer) — only
                // editors see and distribute quarantined families.
                ExcludeUncategorized: !_accessControl.IsEditorRole);

            stageSw.Restart();
            var results = await _catalogProvider.SearchAsync(query, ct);
            SmartConLogger.Freeze($"LoadTreeAsync: SearchAsync took {stageSw.ElapsedMilliseconds}ms, results={results.Count}");
            // #187 (M1): gates the presence refresh on document switches
            // that do NOT reload the tree (OnActiveDocumentChanged).
            _treeLoadedOnce = true;

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
            // Import Validation Gate: quarantine zone — the node exists
            // only for editor roles; read-only roles neither see the node
            // nor receive uncategorized rows (query filter above).
            if (_accessControl.IsEditorRole)
            {
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
                    RevitCategory = item.RevitCategory,
                    ActiveRevitMajorVersion = item.ActiveRevitMajorVersion,
                    MinRevitMajorVersion = item.MinRevitMajorVersion,
                }, _assetService, isStale: isStale, staleReason: staleReason,
                    currentRevitVersion: CurrentRevitVersion, searchText: SearchText));
            }
            _noCategoryNode.FamilyCount = uncategorized.Count;
            // Mirror the logic from BuildCategoryNode so the _noCategoryNode
            // expands on search exactly like ordinary category nodes do, and
            // stays open after the last family is removed when the user already
            // had it expanded.
            if (expandAll)
            {
                if (_noCategoryNode.FamilyCount > 0)
                    _noCategoryNode.IsExpanded = true;
            }
            else if (expandedIds.Contains("__no_category__"))
            {
                _noCategoryNode.IsExpanded = true;
            }
            _noCategoryNode.AttachCollapseTracking();
            rootNodes.Add(_noCategoryNode);
            }
            else
            {
                _noCategoryNode = null;
            }

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

            // E5 (#213, ADR-067): paperclip indicator on leaves referenced by
            // any parent's version — one batch reverse query for the tree.
            try
            {
                await AttachDependencyIndicatorsAsync(rootNodes, ct);
            }
            catch (Exception ex)
            {
                using var _scope = SmartConLogger.BeginScope("LoadTreeAsync", ("Stage", "AttachDependencyIndicatorsAsync"));
                SmartConLogger.Warn($"failed: {ex.Message} [Action: нажмите Refresh чтобы перезагрузить дерево, проверьте БД каталога]");
            }

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
                CollapseAll(rootNodes);
                RestoreExpandedState(rootNodes, _savedExpandedCategoryIds);
                RestoreExpandedFamilies(rootNodes, _savedExpandedFamilyIds);
                _savedExpandedCategoryIds.Clear();
                _savedExpandedFamilyIds.Clear();
            }

            _previousLoadWasSearch = expandAll;

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

            // #259: compliance badges follow the same session-snapshot contract —
            // a tree rebuild re-applies the cached verdicts instead of wiping them
            // (and drops verdicts whose category no longer matches the leaf).
            stageSw.Restart();
            ApplyComplianceResultsToTree(Array.Empty<ComplianceCheckResult>());
            SmartConLogger.Freeze($"LoadTreeAsync: ApplyComplianceResultsToTree took {stageSw.ElapsedMilliseconds}ms");

            // #187: project-presence badges on system type nodes — one
            // CollectTypes pass over the catalog's system categories, then a
            // (family, name) set lookup per node. Cheap: a single collector
            // per tree load, refreshed with every LoadTreeAsync (import,
            // sync, stale check, DB switch).
            //
            // #2 (no flicker): the freshly rebuilt nodes first receive the
            // CACHED snapshot (same document → badges appear instantly, not
            // after the Revit round-trip); the recompute then refreshes them
            // and replaces the cache.
            stageSw.Restart();
            // L1 (review): when no document event has arrived yet the path is
            // UNKNOWN (startup race, #174) — treat it as "same document" so
            // the cached snapshot still prevents flicker; a real mismatch is
            // healed by the recompute below.
            if (_presenceSnapshot is not null
                && (_currentActiveDocumentPath is null
                    || string.Equals(_presenceSnapshot.DocumentPath, _currentActiveDocumentPath, StringComparison.OrdinalIgnoreCase)))
            {
                ApplyPresenceSnapshot(_presenceSnapshot);
            }
            await RecomputePresenceAsync(ct).ConfigureAwait(true);
            SmartConLogger.Freeze($"LoadTreeAsync: RefreshSystemTypeProjectPresence took {stageSw.ElapsedMilliseconds}ms");

            // #133: routing-phantom badges (rules referencing families that
            // left the catalog) — cheap catalog pass, never fails the load.
            await ApplyRoutingHealthToTreeAsync(ct).ConfigureAwait(true);
            SmartConLogger.Freeze($"LoadTreeAsync: ApplyRoutingHealthToTreeAsync took {stageSw.ElapsedMilliseconds}ms");
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
                    RevitCategory = item.RevitCategory,
                    ActiveRevitMajorVersion = item.ActiveRevitMajorVersion,
                    MinRevitMajorVersion = item.MinRevitMajorVersion,
                }, _assetService, isStale: isStale, staleReason: staleReason,
                    currentRevitVersion: CurrentRevitVersion, searchText: SearchText));
            }
        }

        vm.FamilyCount = familyCount;
        var isExpandedApplied = false;
        var isExpandedReason = "no-expand";
        // The user's intent to keep this category expanded must survive even when
        // its last family is removed during the same refresh: rebuild with
        // familyCount=0 must not silently drop the IsExpanded state.
        // expandAll=true still respects familyCount so we don't expand empty
        // brand-new branches that just appeared in the catalog.
        if (expandAll)
        {
            if (familyCount > 0)
            {
                vm.IsExpanded = true;
                isExpandedApplied = true;
                isExpandedReason = "expandAll=true";
            }
        }
        else if (expandedIds is not null && expandedIds.Contains(catNode.Id))
        {
            vm.IsExpanded = true;
            isExpandedApplied = true;
            isExpandedReason = $"in-expandedIds";
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
    /// E5 (#213, ADR-067): выставляет <see cref="FamilyLeafNodeViewModel.IsDependencyReferenced"/>
    /// и строки «родитель (версии)» для диалога деталей (#210) одним batch
    /// reverse-запросом (<see cref="IFamilyDependencyRepository.GetReferencingParentsBatchAsync"/>)
    /// на всё дерево. Индикатор пересчитывается с каждой перезагрузкой дерева
    /// (импорт / удаление / MakeActive идут через LoadTreeAsync).
    /// </summary>
    private async Task AttachDependencyIndicatorsAsync(
        ObservableCollection<CatalogTreeNodeViewModel> rootNodes, CancellationToken ct)
    {
        var leaves = new List<FamilyLeafNodeViewModel>();
        CollectFamilyLeaves(rootNodes, leaves);
        if (leaves.Count == 0) return;

        var batch = await _familyDependencyRepository.GetReferencingParentsBatchAsync(
            leaves.Select(l => l.CatalogItemId).ToList(), ct);

        // E2 (#209): amber "требует переимпорта" badge — the current
        // version embeds a child version that is no longer the child's
        // active one. Same batch pass, same invalidation points.
        var driftBatch = await _familyDependencyRepository.GetDependencyDriftBatchAsync(
            leaves.Select(l => l.CatalogItemId).ToList(), ct);

        foreach (var leaf in leaves)
        {
            if (batch.TryGetValue(leaf.CatalogItemId, out var references))
            {
                leaf.IsDependencyReferenced = true;
                // #210: сырые строки «Родитель (версии)» — буллет-список
                // в диалоге деталей статуса (больше не длинный тултип).
                leaf.DependencyReferencedLines =
                    DependencyGuardText.FormatReferenceLines(references, includeCurrentMark: false);
            }

            if (driftBatch.TryGetValue(leaf.CatalogItemId, out var drifts))
            {
                leaf.HasOutdatedDependencies = true;
                leaf.OutdatedDependencyLines = drifts
                    .Select(d => $"{d.ChildName} ({d.EmbeddedVersionLabel} → {d.CurrentVersionLabel})")
                    .ToList();
            }
        }
    }

    private static void CollectFamilyLeaves(
        ObservableCollection<CatalogTreeNodeViewModel> nodes, List<FamilyLeafNodeViewModel> leaves)
    {
        foreach (var node in nodes)
        {
            if (node is FamilyLeafNodeViewModel leaf)
                leaves.Add(leaf);
            CollectFamilyLeaves(node.Children, leaves);
        }
    }

}
