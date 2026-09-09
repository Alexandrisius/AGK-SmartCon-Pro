using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.Stale;

internal sealed partial class StaleDetector
{
    /// <summary>
    /// System-family branch of <see cref="CheckCategoryAsync"/> (Issue #104):
    /// matches each system catalog item's types (from <c>family_types</c>)
    /// against project <c>ElementType</c> elements by (type name, category
    /// ordinal) and aggregates one verdict per item.
    /// </summary>
    private async Task<IReadOnlyList<StaleCheckResult>> CheckSystemItemsAsync(
        IReadOnlyList<FamilyCatalogItem> systemItems,
        Document doc,
        int targetRevit,
        IProgress<StaleCheckProgress>? progress,
        int doneOffset,
        CancellationToken ct)
    {
        var typeNamesByItem = await _typeRepository
            .GetAllTypesBatchAsync(systemItems.Select(i => i.Id), ct)
            .ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var categoryOrdinals = systemItems
            .Select(i => i.RevitCategoryId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        if (categoryOrdinals.Count == 0) return Array.Empty<StaleCheckResult>();

        var projectTypes = await _awaitable.RaiseAsync(
            _ => _systemTypeFinder.CollectTypes(doc, categoryOrdinals),
            ct).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();

        var typesByCategoryAndName = new Dictionary<(int Category, string Name), List<(string? FamilyKey, string? Family, ElementId Id)>>();
        foreach (var location in projectTypes)
        {
            // Names are matched case-insensitively (same semantics as
            // ISystemTypeFinder.FindTypeByName) — the tuple key carries the
            // upper-invariant form so OrdinalIgnoreCase applies to lookups.
            // #183: one (category, name) key maps to a LIST of candidates —
            // "Стандарт" exists in both conduit families; the descriptor's
            // family disambiguates (legacy null family → first candidate,
            // the pre-#183 behaviour).
            // #190 (ADR-064): each candidate carries the locale-invariant
            // key AND the localized name — descriptors with a key match by
            // key, legacy descriptors fall back to the name.
            var key = (location.CategoryOrdinal, location.TypeName.ToUpperInvariant());
            if (!typesByCategoryAndName.TryGetValue(key, out var candidates))
            {
                candidates = new List<(string?, string?, ElementId)>();
                typesByCategoryAndName[key] = candidates;
            }
            candidates.Add((location.FamilyKey, location.FamilyName, location.TypeId));
        }

        var matched = new List<(FamilyCatalogItem Item, List<ElementId> TypeIds)>();
        // Audit (B1): items that previously matched but whose types vanished
        // from the project — their snapshot entries must be REMOVED, else
        // MergeInto keeps the old stale verdict (phantom badge until
        // InvalidateCache).
        var vanishedItemIds = new List<string>();
        // #187: per-descriptor matches kept for the per-type stale map
        // (orange presence dot) — the aggregate verdict alone loses which
        // concrete type is outdated.
        var matchedByDescriptor = new List<(FamilyCatalogItem Item, FamilyTypeDescriptor Descriptor, ElementId TypeId)>();
        foreach (var item in systemItems)
        {
            if (!item.RevitCategoryId.HasValue) continue;
            if (!typeNamesByItem.TryGetValue(item.Id, out var descriptors) || descriptors.Count == 0)
                continue;

            var ids = new List<ElementId>();
            foreach (var descriptor in descriptors)
            {
                if (!typesByCategoryAndName.TryGetValue(
                        (item.RevitCategoryId.Value, descriptor.Name.ToUpperInvariant()), out var candidates))
                {
                    continue;
                }

                if (descriptor.FamilyKey is not null || descriptor.FamilyName is not null)
                {
                    // Exact identity match — never read the marker of a
                    // foreign family's type. #190 (ADR-064): key first
                    // (locale-invariant), legacy descriptors (pre-V27 rows
                    // have no key) match by the localized family name.
                    foreach (var (familyKey, family, id) in candidates)
                    {
                        var isMatch = descriptor.FamilyKey is not null
                            ? string.Equals(familyKey, descriptor.FamilyKey, StringComparison.OrdinalIgnoreCase)
                            : string.Equals(family, descriptor.FamilyName, StringComparison.OrdinalIgnoreCase);
                        if (isMatch)
                        {
                            ids.Add(id);
                            matchedByDescriptor.Add((item, descriptor, id));
                            break;
                        }
                    }
                }
                else
                {
                    // Legacy row without family (pre-V26): first candidate,
                    // same as the pre-#183 name-only match.
                    ids.Add(candidates[0].Id);
                    matchedByDescriptor.Add((item, descriptor, candidates[0].Id));
                }
            }
            if (ids.Count > 0)
            {
                matched.Add((item, ids));
            }
            else
            {
                vanishedItemIds.Add(item.Id);
            }
        }

        if (vanishedItemIds.Count > 0)
        {
            lock (_cacheLock)
            {
                foreach (var id in vanishedItemIds)
                {
                    _systemTypeStaleByType.Remove(id);
                }
                if (_cachedSnapshot is not null)
                {
                    _cachedSnapshot = StaleSnapshotLogic.RemoveFrom(_cachedSnapshot, vanishedItemIds, _clock.UtcNow);
                }
            }
        }

        if (matched.Count == 0) return Array.Empty<StaleCheckResult>();

        var allIds = matched.SelectMany(m => m.TypeIds).Distinct().ToList();
        var markers = await _awaitable.RaiseAsync(
            _ => _systemTypeStore.ReadManyFromTypes(doc, allIds),
            ct).ConfigureAwait(true);

        // ADR-072 World B: routing fingerprint probe per matched MEP item —
        // the marker can be current while the catalog links drifted.
        var driftByItem = new Dictionary<string, IReadOnlyDictionary<string, bool>>(StringComparer.Ordinal);
        foreach (var group in matchedByDescriptor.GroupBy(m => m.Item.Id))
        {
            var drift = await ComputeRoutingDriftAsync(
                    doc,
                    group.First().Item,
                    group.Select(m => (m.Descriptor, m.TypeId)).ToList(),
                    ct)
                .ConfigureAwait(false);
            if (drift is not null)
                driftByItem[group.Key] = drift;
        }

        var results = new List<StaleCheckResult>(matched.Count);
        var sysProgressTotal = doneOffset + matched.Count;
        var sysProgressDone = 0;
        foreach (var (item, typeIds) in matched)
        {
            var itemMarkers = typeIds
                .Select(id => markers.TryGetValue(id, out var m) ? m : null)
                .ToList();
            var (isStale, reason, loadedLabel) = AggregateSystemTypeMarkers(item, itemMarkers, targetRevit);
            if (!isStale
                && driftByItem.TryGetValue(item.Id, out var itemDrift)
                && itemDrift.Values.Any(v => v))
            {
                isStale = true;
                reason = StaleReason.RoutingDrift;
            }
            results.Add(new StaleCheckResult(
                item.Id,
                item.Name,
                item.CurrentVersionLabel,
                loadedLabel,
                isStale,
                reason));

            sysProgressDone++;
            progress?.Report(new StaleCheckProgress(doneOffset + sysProgressDone, sysProgressTotal, item.Name));
        }

        // #187: per-type stale map — one verdict per (family, name) type so
        // the tree can paint the ORANGE presence dot on the exact outdated
        // type, not just the leaf roll-up. #253: a VersionMismatch dot is
        // refined from the catalog DB (content hashes + shared sections) —
        // computed BEFORE the lock because the refinement awaits DB reads.
        var analyticsMemo = new Dictionary<string, (IReadOnlyList<FamilyTypeHashEntry>?, IReadOnlyList<FamilyTypeHashEntry>?, IReadOnlyList<string>)>(StringComparer.Ordinal);
        var refinedTypeMaps = new Dictionary<string, Dictionary<string, bool>>(StringComparer.Ordinal);
        foreach (var group in matchedByDescriptor.GroupBy(m => m.Item.Id))
        {
            var item = group.First().Item;
            var typeMap = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var (_, descriptor, typeId) in group)
            {
                markers.TryGetValue(typeId, out var marker);
                // Stress test 2026-08-05 (semantics change): a missing
                // marker is NOT stale — template-native types have
                // unknown provenance, not proven outdatedness. Only a
                // marker mismatch paints the orange dot.
                var reason = marker is null
                    ? StaleReason.None
                    : SystemTypeStaleLogic.ComputeReason(
                        marker, item.Id, item.CurrentVersionLabel, targetRevit);
                var isTypeStale = reason != StaleReason.None;
                if (isTypeStale
                    && reason == StaleReason.VersionMismatch
                    && marker is not null
                    && item.CurrentVersionLabel is not null
                    && _contentHashAnalytics is not null)
                {
                    var refined = await RefineSystemTypeStaleAsync(
                        item.Id, descriptor, marker.VersionLabel, item.CurrentVersionLabel, analyticsMemo, ct)
                        .ConfigureAwait(false);
                    if (refined.HasValue)
                    {
                        isTypeStale = refined.Value;
                    }
                }
                var typeKey = BuildSystemTypeKey(descriptor.FamilyKey, descriptor.FamilyName, descriptor.Name);
                if (!isTypeStale
                    && driftByItem.TryGetValue(item.Id, out var typeDrift)
                    && typeDrift.TryGetValue(typeKey, out var drifted)
                    && drifted)
                {
                    isTypeStale = true;
                }
                typeMap[typeKey] = isTypeStale;
            }
            refinedTypeMaps[group.Key] = typeMap;
        }
        lock (_cacheLock)
        {
            foreach (var kvp in refinedTypeMaps)
            {
                _systemTypeStaleByType[kvp.Key] = kvp.Value;
            }
        }

        return results;
    }

    public async Task<StaleCheckResult?> CheckSystemFamilyAsync(
        string catalogItemId,
        string displayName,
        Document doc,
        CancellationToken ct)
    {
        using var _scope = SmartConLogger.BeginScope(
            "StaleDetection",
            ("Method", nameof(CheckSystemFamilyAsync)),
            ("CatalogItemId", catalogItemId));

#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(doc);
#else
        if (doc is null) throw new ArgumentNullException(nameof(doc));
#endif

        var catalogItem = await _catalog.GetItemAsync(catalogItemId, ct).ConfigureAwait(false);
        if (catalogItem is null)
        {
            var notInCatalog = new StaleCheckResult(
                catalogItemId, displayName, null, null, true, StaleReason.NotInCatalog);
            MergeSingleResult(notInCatalog);
            return notInCatalog;
        }

        var descriptors = await _typeRepository
            .GetTypesForItemAsync(catalogItemId, ct)
            .ConfigureAwait(false);
        if (descriptors.Count == 0)
        {
            SmartConLogger.Info(
                $"CheckSystemFamily: '{displayName}' has no types in the catalog. " +
                "[Action: skipped — reimport the mini-project to rebuild the type list]");
            return null;
        }

        var categoryOrdinal = catalogItem.RevitCategoryId;
        var foundPairs = await _awaitable.RaiseAsync(
            _ =>
            {
                var pairs = new List<(FamilyTypeDescriptor Descriptor, ElementId TypeId)>();
                // #183: match by full identity (family, name) — with two
                // conduit families sharing "Стандарт" a name-only lookup
                // would return the SAME first match twice and never read
                // the marker of the other family's type. Legacy rows
                // (family NULL) degrade to the pre-#183 first-name match.
                foreach (var descriptor in descriptors)
                {
                    var id = _systemTypeFinder.FindTypeByName(
                        doc, descriptor.Name, categoryOrdinal, descriptor.FamilyName, descriptor.FamilyKey);
                    if (id is not null && !pairs.Any(p => p.TypeId == id))
                    {
                        pairs.Add((descriptor, id));
                    }
                }
                return pairs;
            }, ct).ConfigureAwait(true);

        if (foundPairs.Count == 0)
        {
            // Audit (B1): none of the item's types are in the project — the
            // family vanished; drop its snapshot entry and per-type map so
            // no phantom stale badge survives until InvalidateCache.
            lock (_cacheLock)
            {
                _systemTypeStaleByType.Remove(catalogItemId);
                if (_cachedSnapshot is not null)
                {
                    _cachedSnapshot = StaleSnapshotLogic.RemoveFrom(
                        _cachedSnapshot, new[] { catalogItemId }, _clock.UtcNow);
                }
            }
            SmartConLogger.Info(
                $"CheckSystemFamily: none of {descriptors.Count} types of '{displayName}' " +
                "are present in the project — snapshot entry dropped (not loaded). " +
                "[Action: load the types first]");
            return null;
        }

        var foundIds = foundPairs.Select(p => p.TypeId).ToList();
        var markers = await _awaitable.RaiseAsync(
            _ => _systemTypeStore.ReadManyFromTypes(doc, foundIds),
            ct).ConfigureAwait(true);

        var itemMarkers = foundIds
            .Select(id => markers.TryGetValue(id, out var m) ? m : null)
            .ToList();
        var targetRevit = ResolveTargetRevit();
        var (isStaleAgg, reasonAgg, loadedLabel) = AggregateSystemTypeMarkers(catalogItem, itemMarkers, targetRevit);
        var isStale = isStaleAgg;
        var reason = reasonAgg;

        // #187: per-type stale map (orange presence dot per exact type).
        // #253: VersionMismatch dots refined from the catalog DB (same
        // semantics as the batch path).
        var analyticsMemo = new Dictionary<string, (IReadOnlyList<FamilyTypeHashEntry>?, IReadOnlyList<FamilyTypeHashEntry>?, IReadOnlyList<string>)>(StringComparer.Ordinal);
        var typeMap = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (descriptor, typeId) in foundPairs)
        {
            markers.TryGetValue(typeId, out var marker);
            var typeReason = marker is null
                ? StaleReason.None
                : SystemTypeStaleLogic.ComputeReason(
                    marker, catalogItemId, catalogItem.CurrentVersionLabel, targetRevit);
            var isTypeStale = typeReason != StaleReason.None;
            if (isTypeStale
                && typeReason == StaleReason.VersionMismatch
                && marker is not null
                && catalogItem.CurrentVersionLabel is not null
                && _contentHashAnalytics is not null)
            {
                var refined = await RefineSystemTypeStaleAsync(
                    catalogItemId, descriptor, marker.VersionLabel, catalogItem.CurrentVersionLabel, analyticsMemo, ct)
                    .ConfigureAwait(false);
                if (refined.HasValue)
                {
                    isTypeStale = refined.Value;
                }
            }
            typeMap[BuildSystemTypeKey(descriptor.FamilyKey, descriptor.FamilyName, descriptor.Name)] = isTypeStale;
        }

        // ADR-072 World B: the marker can be perfectly current while the
        // routing links drifted (editor save / manual project edit) — the
        // fingerprint probe upgrades the verdict to RoutingDrift. Audit M7:
        // the probe runs even when the family is ALREADY stale by content
        // (batch-path parity) — the per-type map must show the drifted type
        // right after an editor save; the item-level reason, though, upgrades
        // to RoutingDrift only when no stronger content reason exists.
        var routingDrift = await ComputeRoutingDriftAsync(doc, catalogItem, foundPairs, ct)
            .ConfigureAwait(false);
        if (routingDrift is not null)
        {
            foreach (var pair in routingDrift)
            {
                if (!pair.Value) continue;
                typeMap[pair.Key] = true;
                if (!isStale)
                {
                    isStale = true;
                    reason = StaleReason.RoutingDrift;
                }
            }
        }

        lock (_cacheLock)
        {
            _systemTypeStaleByType[catalogItemId] = typeMap;
        }

        var result = new StaleCheckResult(
            catalogItemId,
            displayName,
            catalogItem.CurrentVersionLabel,
            loadedLabel,
            isStale,
            reason);
        MergeSingleResult(result);
        SmartConLogger.Info(
            $"CheckSystemFamily: '{displayName}' IsStale={isStale} Reason={reason} " +
            $"({foundIds.Count}/{descriptors.Count} types found in project).");
        return result;
    }

    /// <summary>
    /// Aggregates per-type markers of one system catalog item into a single
    /// verdict. Thin wrapper over the pure <see cref="SystemTypeStaleLogic"/>
    /// (Core) so the aggregation is unit-testable without Revit.
    /// </summary>
    internal static (bool IsStale, StaleReason Reason, string? LoadedLabel) AggregateSystemTypeMarkers(
        FamilyCatalogItem item,
        IReadOnlyList<FamilyVersion?> markers,
        int targetRevit)
    {
        return SystemTypeStaleLogic.Aggregate(
            markers, item.Id, item.CurrentVersionLabel, targetRevit);
    }
}
