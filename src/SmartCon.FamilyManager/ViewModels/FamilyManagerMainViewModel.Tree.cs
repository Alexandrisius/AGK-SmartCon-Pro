using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
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
            // #187 (M1): kept for the presence refresh on document switches
            // that do NOT reload the tree (OnActiveDocumentChanged).
            _lastTreeCatalogItems = results;

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

    /// <summary>
    /// #187: immutable presence snapshot of ONE document — which catalog
    /// types/families are loaded in it. Cached in
    /// <see cref="_presenceSnapshot"/> and re-applied to freshly rebuilt
    /// tree nodes when the document has not changed (#2: badges must not
    /// flicker on every LoadTreeAsync).
    /// </summary>
    private sealed class ProjectPresenceSnapshot
    {
        public required string DocumentPath { get; init; }
        // #190 (review fix): the category ordinal is part of the identity —
        // the "Single" key of one-family categories (pipes, insulations, …)
        // is identical across categories, so a category-less set would
        // cross-match same-named types of different categories.
        public required HashSet<(int Category, string Family, string Name)> SystemTypes { get; init; }
        public required Dictionary<int, HashSet<string>> SystemTypeNamesByCategory { get; init; }
        public required Dictionary<string, int> SystemCategoryOrdinalByItem { get; init; }
        public required HashSet<string> LoadableFamilies { get; init; }
        public required HashSet<(string Family, string Type)> LoadableTypes { get; init; }
    }

    /// <summary>
    /// #187: computes the presence snapshot of the ACTIVE document. MUST run
    /// inside the awaitable ExternalEvent (Revit API: CollectTypes +
    /// CollectLoadedFamilySymbols). A slice is skipped when the catalog
    /// carries no items of that kind (no pointless collector passes).
    /// </summary>
    private ProjectPresenceSnapshot ComputePresenceSnapshot(
        Autodesk.Revit.DB.Document doc, IReadOnlyList<FamilyCatalogItem> catalogItems)
    {
        var systemItems = catalogItems
            .Where(i => i.FamilySource == "system" && i.RevitCategoryId.HasValue)
            .ToList();

        // System slice: all ElementTypes of the catalog's system categories.
        var systemTypes = new HashSet<(int Category, string Family, string Name)>();
        var systemTypeNamesByCategory = new Dictionary<int, HashSet<string>>();
        if (systemItems.Count > 0)
        {
            var ordinals = systemItems.Select(i => i.RevitCategoryId!.Value).Distinct().ToList();
            var locations = _systemTypeFinder.CollectTypes(doc, ordinals);
            foreach (var l in locations)
            {
                // #190 (ADR-064): register BOTH identity forms — the
                // locale-invariant key (new V27 rows match by it) and the
                // localized name (legacy pre-V27 rows match by it). The set
                // is a lookup structure, not a store, so dual entries are
                // safe: a node matches iff ITS descriptor token is present.
                // The category ordinal prefixes every entry — "Single" is
                // the same token across one-family categories.
                if (l.FamilyKey is not null)
                {
                    systemTypes.Add((l.CategoryOrdinal, l.FamilyKey.ToUpperInvariant(), l.TypeName.ToUpperInvariant()));
                }
                if (l.FamilyName is not null)
                {
                    systemTypes.Add((l.CategoryOrdinal, l.FamilyName.ToUpperInvariant(), l.TypeName.ToUpperInvariant()));
                }
                if (!systemTypeNamesByCategory.TryGetValue(l.CategoryOrdinal, out var set))
                {
                    set = new HashSet<string>();
                    systemTypeNamesByCategory[l.CategoryOrdinal] = set;
                }
                set.Add(l.TypeName.ToUpperInvariant());
            }
        }

        // Loadable slice: every FamilySymbol (family loaded + symbol loaded).
        // Skipped entirely for a system-only catalog (L5 review).
        var loadableFamilies = new HashSet<string>(StringComparer.Ordinal);
        var loadableTypes = new HashSet<(string Family, string Type)>();
        if (catalogItems.Any(i => i.FamilySource != "system"))
        {
            foreach (var (familyName, typeName) in _familyFinder.CollectLoadedFamilySymbols(doc))
            {
                loadableFamilies.Add(familyName.ToUpperInvariant());
                loadableTypes.Add((familyName.ToUpperInvariant(), typeName.ToUpperInvariant()));
            }
        }

        return new ProjectPresenceSnapshot
        {
            DocumentPath = doc.PathName ?? string.Empty,
            SystemTypes = systemTypes,
            SystemTypeNamesByCategory = systemTypeNamesByCategory,
            SystemCategoryOrdinalByItem = systemItems
                .Where(i => i.RevitCategoryId.HasValue)
                .ToDictionary(i => i.Id, i => i.RevitCategoryId!.Value),
            LoadableFamilies = loadableFamilies,
            LoadableTypes = loadableTypes,
        };
    }

    /// <summary>
    /// #187: applies a presence snapshot to the CURRENT tree nodes — pure
    /// in-memory pass (no Revit API). System leaf badges roll up from their
    /// types (#1: a system family has no LoadFamily — it "is in the project"
    /// when at least one of its types is).
    /// </summary>
    private int ApplyPresenceSnapshot(ProjectPresenceSnapshot snapshot)
    {
        var marked = 0;
        foreach (var leaf in EnumerateAllLeaves(TreeNodes.OfType<CategoryNodeViewModel>()))
        {
            var familyKey = leaf.DisplayName.ToUpperInvariant();
            if (leaf.FamilySource == "system")
            {
                // #187: per-type stale map (orange dot) — available after a
                // stale check ran for this item; null before the first check.
                var staleMap = _staleDetector.GetSystemTypeStaleMap(leaf.CatalogItemId);

                var anyTypePresent = false;
                foreach (var typeNode in leaf.Children.OfType<FamilyTypeNodeViewModel>())
                {
                    // Virtual nodes carry the leaf's display name as TypeName —
                    // a presence lookup by it is meaningless.
                    if (typeNode.IsVirtual) continue;

                    bool isPresent;
                    // #190 (ADR-064): effective identity token — the
                    // locale-invariant key when the row has one (V27+), the
                    // localized family name for legacy rows. The snapshot
                    // set carries both forms (see ComputePresenceSnapshot);
                    // the category ordinal disambiguates one-family tokens.
                    var familyToken = typeNode.FamilyKey ?? typeNode.FamilyName;
                    if (!snapshot.SystemCategoryOrdinalByItem.TryGetValue(leaf.CatalogItemId, out var ordinal))
                    {
                        isPresent = false;
                    }
                    else if (familyToken is not null)
                    {
                        isPresent = snapshot.SystemTypes.Contains(
                            (ordinal, familyToken.ToUpperInvariant(), typeNode.TypeName.ToUpperInvariant()));
                    }
                    else
                    {
                        isPresent = snapshot.SystemTypeNamesByCategory.TryGetValue(ordinal, out var names)
                            && names.Contains(typeNode.TypeName.ToUpperInvariant());
                    }
                    if (isPresent) { anyTypePresent = true; marked++; }
                    typeNode.IsInProject = isPresent;
                    typeNode.IsStaleInProject = isPresent
                        && staleMap is not null
                        && staleMap.TryGetValue(
                            StaleDetector.BuildSystemTypeKey(typeNode.FamilyKey, typeNode.FamilyName, typeNode.TypeName), out var stale)
                        && stale;
                }
                leaf.IsInProject = anyTypePresent;
            }
            else
            {
                var leafPresent = snapshot.LoadableFamilies.Contains(familyKey);
                leaf.IsInProject = leafPresent;

                foreach (var typeNode in leaf.Children.OfType<FamilyTypeNodeViewModel>())
                {
                    if (typeNode.IsVirtual) continue;

                    var typePresent = snapshot.LoadableTypes.Contains(
                        (familyKey, typeNode.TypeName.ToUpperInvariant()));
                    if (typePresent) marked++;
                    typeNode.IsInProject = typePresent;
                    // #187: loadable stale is leaf-scoped (the whole family
                    // version is outdated) — every loaded type of a stale
                    // family gets the orange dot.
                    typeNode.IsStaleInProject = typePresent && leaf.IsStale;
                }
            }
        }
        return marked;
    }

    /// <summary>
    /// #187: re-applies the per-type stale maps (orange dots) after a stale
    /// check updated them — the presence snapshot is untouched (types did
    /// not move in/out of the project, only their freshness changed).
    /// </summary>
    internal void ApplySystemTypeStaleMaps()
    {
        foreach (var leaf in EnumerateAllLeaves(TreeNodes.OfType<CategoryNodeViewModel>()))
        {
            if (leaf.FamilySource != "system")
            {
                // Loadable: leaf-scoped stale — refresh the per-type dots
                // with the leaf's own IsStale flag.
                foreach (var typeNode in leaf.Children.OfType<FamilyTypeNodeViewModel>())
                {
                    if (typeNode.IsVirtual) continue;
                    typeNode.IsStaleInProject = typeNode.IsInProject && leaf.IsStale;
                }
                continue;
            }
            var staleMap = _staleDetector.GetSystemTypeStaleMap(leaf.CatalogItemId);
            foreach (var typeNode in leaf.Children.OfType<FamilyTypeNodeViewModel>())
            {
                if (typeNode.IsVirtual) continue;
                typeNode.IsStaleInProject = typeNode.IsInProject
                    && staleMap is not null
                    && staleMap.TryGetValue(
                        StaleDetector.BuildSystemTypeKey(typeNode.FamilyKey, typeNode.FamilyName, typeNode.TypeName), out var stale)
                    && stale;
            }
        }
    }

    /// <summary>
    /// #187: re-evaluates presence against the CURRENT active document and
    /// caches the snapshot. The whole pass runs inside the awaitable
    /// ExternalEvent — <c>RevitContext.GetDocument()</c> MUST be called on
    /// the Revit thread, not on a thread-pool continuation of the
    /// ViewActivated handler (#3: badges did not refresh on document switch).
    /// The snapshot is computed over the FULL catalog (never the
    /// search-scoped list) — a search-filtered snapshot would drop slices
    /// and re-introduce flicker when the search is cleared (review M).
    /// </summary>
    private async Task RecomputePresenceAsync(CancellationToken ct)
    {
        using var _scope = SmartConLogger.BeginScope("FMPresence",
            ("Method", nameof(RecomputePresenceAsync)));
        try
        {
            var allItems = await _catalogProvider.SearchAsync(
                new FamilyCatalogQuery(
                    SearchText: null,
                    CategoryFilter: null,
                    StatusFilter: null,
                    Tags: null,
                    Sort: FamilyCatalogSort.NameAsc,
                    Offset: 0,
                    Limit: int.MaxValue,
                    ExcludeUncategorized: !_accessControl.IsEditorRole),
                ct).ConfigureAwait(true);

            var snapshot = await _awaitableEvent.RaiseAsync(_ =>
            {
                var doc = _revitContext.GetDocument();
                // A family document (.rfa) is not a project — presence badges
                // are meaningless against it (and would wipe to zero). Keep
                // the current badges; they re-evaluate on the next project.
                if (doc.IsFamilyDocument) return null;
                return ComputePresenceSnapshot(doc, allItems);
            }, ct).ConfigureAwait(true);

            if (snapshot is null)
            {
                SmartConLogger.Debug("Presence recompute skipped — active document is a family");
                return;
            }

            _presenceSnapshot = snapshot;
            var marked = ApplyPresenceSnapshot(snapshot);
            SmartConLogger.Debug(
                $"Presence: snapshot '{System.IO.Path.GetFileName(snapshot.DocumentPath)}' — {marked} badge(s) set");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // No active document (all closed) or a collector failure — badges
            // stay as they were; the next LoadTree re-evaluates.
            SmartConLogger.Debug($"Presence recompute skipped: {ex.Message}");
        }
    }

    /// <summary>
    /// #187 (M1): re-evaluates presence badges against the CURRENT active
    /// document without rebuilding the tree — used on document switches that
    /// do not trigger a LoadTreeAsync (same active database).
    /// </summary>
    internal async Task RefreshSystemTypeProjectPresenceSafeAsync()
    {
        if (_lastTreeCatalogItems is null) return;
        await RecomputePresenceAsync(CancellationToken.None);
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
