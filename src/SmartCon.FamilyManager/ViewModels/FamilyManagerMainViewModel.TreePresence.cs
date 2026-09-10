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

                // #249 (Phase 2): per-type drift map from the content
                // verification — the orange dot lands on the exact drifted
                // types. Null (no per-type proof) → the pre-#249 leaf-scoped
                // fallback: every loaded type of a stale family gets the dot.
                var loadableStaleMap = _staleDetector.GetLoadableTypeStaleMap(leaf.CatalogItemId);

                foreach (var typeNode in leaf.Children.OfType<FamilyTypeNodeViewModel>())
                {
                    bool typePresent;
                    if (typeNode.IsVirtual)
                    {
                        // #212 (ADR-066 follow-up): the virtual node represents
                        // the typeless family itself — the synthetic
                        // "<default>" type never exists in a project (Revit
                        // auto-creates a family-named symbol on load, #172).
                        // Its presence is the family's presence; skipping it
                        // left typeless fittings grey forever.
                        typePresent = leafPresent;
                    }
                    else
                    {
                        typePresent = snapshot.LoadableTypes.Contains(
                            (familyKey, typeNode.TypeName.ToUpperInvariant()));
                    }
                    if (typePresent) marked++;
                    typeNode.IsInProject = typePresent;
                    typeNode.IsStaleInProject = typePresent
                        && IsLoadableTypeStale(loadableStaleMap, typeNode, leaf);
                }
            }
        }
        return marked;
    }

    /// <summary>
    /// #249 (Phase 2): per-type stale verdict for a LOADABLE type node.
    /// With a content-proof map the dot follows the map (virtual nodes —
    /// the typeless family itself — have no per-type identity and always
    /// take the leaf verdict; a real type absent from the map was
    /// compared and did NOT drift). Without a map (no proof ran) the
    /// pre-#249 leaf-scoped verdict applies.
    /// </summary>
    private static bool IsLoadableTypeStale(
        IReadOnlyDictionary<string, bool>? loadableStaleMap,
        FamilyTypeNodeViewModel typeNode,
        FamilyLeafNodeViewModel leaf)
    {
        if (loadableStaleMap is null || typeNode.IsVirtual)
        {
            return leaf.IsStale;
        }
        return loadableStaleMap.TryGetValue(typeNode.TypeName, out var stale) && stale;
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
                // Loadable: #249 (Phase 2) per-type map when a content
                // proof exists; otherwise the leaf-scoped verdict (the
                // pre-#249 behaviour). #212: virtual nodes are NOT
                // skipped — the typeless family's node carries the same
                // leaf-scoped verdict as real types (its presence/stale
                // is the family's).
                var loadableStaleMap = _staleDetector.GetLoadableTypeStaleMap(leaf.CatalogItemId);
                foreach (var typeNode in leaf.Children.OfType<FamilyTypeNodeViewModel>())
                {
                    typeNode.IsStaleInProject = typeNode.IsInProject
                        && IsLoadableTypeStale(loadableStaleMap, typeNode, leaf);
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
    /// do not trigger a LoadTreeAsync (same active database). Concurrent
    /// requests coalesce (multi-type DnD fires one placement event per
    /// type): a recompute already in flight sets the trailing flag and the
    /// loop runs once more with the freshest document state. Callers are
    /// marshalled to the UI thread (dispatcher / ConfigureAwait(true)), so
    /// the flags need no interlocking.
    /// </summary>
    internal async Task RefreshSystemTypeProjectPresenceSafeAsync()
    {
        if (!_treeLoadedOnce) return;
        if (_presenceRecomputeInFlight)
        {
            _presenceRecomputePending = true;
            return;
        }

        _presenceRecomputeInFlight = true;
        try
        {
            do
            {
                _presenceRecomputePending = false;
                await RecomputePresenceAsync(CancellationToken.None);
            }
            while (_presenceRecomputePending);
        }
        finally
        {
            _presenceRecomputeInFlight = false;
        }
    }
}
