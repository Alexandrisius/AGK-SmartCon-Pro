using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.Stale;

internal sealed partial class StaleDetector
{
    /// <summary>
    /// #253: refines ONE system type's VersionMismatch dot from the catalog
    /// DB (per-type content hashes of the marker's version vs the current
    /// one + the shared-section rule) — zero document opens, the same
    /// semantics the loadable DB map
    /// (<see cref="ComputeDbPerTypeStaleAsync"/>) applies. Returns
    /// <c>null</c> when the analytics cannot prove anything (pending
    /// backfill / foreign marker label) — the caller keeps the marker-based
    /// dot. <paramref name="memo"/> caches the (types ×2 + shared sections)
    /// triple per (item, marker label) across the item's types and across
    /// items of one check run.
    /// </summary>
    private async Task<bool?> RefineSystemTypeStaleAsync(
        string catalogItemId,
        FamilyTypeDescriptor descriptor,
        string fromVersionLabel,
        string currentVersionLabel,
        Dictionary<string, (IReadOnlyList<FamilyTypeHashEntry>? From, IReadOnlyList<FamilyTypeHashEntry>? To, IReadOnlyList<string> Shared)> memo,
        CancellationToken ct)
    {
        try
        {
            var memoKey = catalogItemId + "|" + fromVersionLabel;
            if (!memo.TryGetValue(memoKey, out var analytics))
            {
                var from = await _contentHashAnalytics!.GetTypeHashesAsync(catalogItemId, fromVersionLabel, ct)
                    .ConfigureAwait(false);
                var to = await _contentHashAnalytics.GetTypeHashesAsync(catalogItemId, currentVersionLabel, ct)
                    .ConfigureAwait(false);
                var shared = await ComputeChangedSharedSectionsAsync(catalogItemId, fromVersionLabel, currentVersionLabel, ct)
                    .ConfigureAwait(false);
                analytics = (from, to, shared);
                memo[memoKey] = analytics;
            }

            var refined = SystemTypeStaleLogic.RefineVersionMismatchWithContent(
                analytics.From,
                analytics.To,
                analytics.Shared,
                SystemTypeIdentityKey.Build(descriptor.FamilyKey, descriptor.FamilyName, descriptor.Name));
            if (refined.HasValue)
            {
                SmartConLogger.Debug(
                    $"CheckEmbedded[{catalogItemId}]: system per-type '{descriptor.Name}' " +
                    $"{fromVersionLabel}→{currentVersionLabel} refined by content hash: stale={refined.Value}" +
                    (analytics.Shared.Count > 0
                        ? $" (shared sections changed: {string.Join(", ", analytics.Shared)})"
                        : string.Empty));
            }
            return refined;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Mirror ComputeDbPerTypeStaleAsync: a transient analytics
            // failure degrades to the marker-based dot — it must never
            // abort the whole stale check.
            SmartConLogger.Warn(
                $"CheckEmbedded[{catalogItemId}]: system per-type content refinement failed: {ex.Message} " +
                "[Action: per-type уточнение отключено для этого типа до следующей «Проверить»; повторите проверку]");
            return null;
        }
    }

    /// <summary>
    /// ADR-072 World B routing drift probe: compares the LIVE routing
    /// preferences of the project's loaded types against the catalog's
    /// item-level routing links via <see cref="RoutingFingerprint"/>
    /// (routing left the content hash, so marker checks are blind to it).
    /// Returns <c>null</c> when the catalog has no opinion (non-MEP
    /// category, no item-level links, missing seams) — the caller keeps
    /// the marker-based verdict; otherwise a per-type drift map keyed by
    /// <see cref="BuildSystemTypeKey"/>.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, bool>?> ComputeRoutingDriftAsync(
        Document doc,
        FamilyCatalogItem item,
        IReadOnlyList<(FamilyTypeDescriptor Descriptor, ElementId TypeId)> pairs,
        CancellationToken ct)
    {
        if (_routingRuleRepository is null || _snapshotExtractor is null)
            return null;
        if (!RoutingGroupCatalog.IsMepCurveCategory(item.RevitCategoryId))
            return null;

        try
        {
            // Same source chain as sync (audit M9): item-level V37 first,
            // the current version's V34 rows as the legacy fallback —
            // otherwise an unhealed legacy window leaves the probe blind
            // while sync still applies the V34 routing.
            var (rules, settings) = await _routingRuleRepository
                .HasAnyForItemAsync(item.Id, ct).ConfigureAwait(false)
                ? await _routingRuleRepository.ReadForItemAsync(item.Id, ct).ConfigureAwait(false)
                : !await _routingRuleRepository.HasRulesForCurrentVersionAsync(item.Id, ct).ConfigureAwait(false)
                    ? default
                    : await _routingRuleRepository.ReadForCurrentVersionAsync(item.Id, ct).ConfigureAwait(false);
            if (rules is null)
                return null;

            // FHV21: segment rules compose from the per-version store of the
            // CURRENT version — the drift fingerprint matches exactly what
            // sync would apply (fittings item-level, segments versioned).
            if (_segmentRuleRepository is not null)
            {
                var perVersionSegments = await _segmentRuleRepository
                    .ReadForCurrentVersionAsync(item.Id, ct).ConfigureAwait(false);
                rules = SegmentRuleComposition.Compose(rules, perVersionSegments);
            }

            // Only pipes carry size ranges in routing (owner decision
            // 2026-08-30). Legacy item rows may still hold the criterion
            // for ducts — normalize both sides so the probe compares like
            // with like (see RoutingFingerprint.WithoutSizeCriteria).
            var includeSizeCriteria = RoutingGroupCatalog.HasSizeCriteria(item.RevitCategoryId);
            RoutingPreferencesSnapshot? NormalizeRouting(RoutingPreferencesSnapshot? snapshot)
                => RoutingFingerprint.WithCanonicalTransitionGroups(
                    includeSizeCriteria ? snapshot : RoutingFingerprint.WithoutSizeCriteria(snapshot));

            var catalogFingerprints = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var setting in settings)
            {
                // Audit L10: legacy rows may carry an EMPTY FamilyKey — the
                // catalog key would degrade to "|NAME" and never match the
                // live "SINGLE|NAME" (silent drift blindness). Resolve the
                // identity from the item's type descriptors — the same
                // source the live side of the comparison uses.
                var descriptor = pairs
                    .FirstOrDefault(p => string.Equals(
                        p.Descriptor?.Name, setting.TypeName, StringComparison.Ordinal))
                    .Descriptor;
                var effectiveKey = !string.IsNullOrEmpty(setting.FamilyKey)
                    ? setting.FamilyKey
                    : descriptor?.FamilyKey;
                catalogFingerprints[BuildSystemTypeKey(effectiveKey, descriptor?.FamilyName, setting.TypeName)] =
                    RoutingFingerprint.Compute(NormalizeRouting(RoutingRuleRecordMapper.ToSnapshot(
                        setting.TypeName, setting.FamilyKey, rules, settings)));
            }
            if (catalogFingerprints.Count == 0)
                return null;

            var extractor = _snapshotExtractor;
            var live = await _awaitable.RaiseAsync(_ =>
            {
                var map = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var (descriptor, typeId) in pairs)
                {
                    map[BuildSystemTypeKey(descriptor.FamilyKey, descriptor.FamilyName, descriptor.Name)] =
                        RoutingFingerprint.Compute(NormalizeRouting(extractor.ExtractSystemTypeRouting(doc, typeId)));
                }
                return map;
            }, ct).ConfigureAwait(true);

            var drift = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var (descriptor, _) in pairs)
            {
                var key = BuildSystemTypeKey(descriptor.FamilyKey, descriptor.FamilyName, descriptor.Name);
                if (!catalogFingerprints.TryGetValue(key, out var catalogFingerprint))
                {
                    drift[key] = false;
                    continue;
                }
                live.TryGetValue(key, out var liveFingerprint);
                // Live-null on an MEP type means the routing READ failed
                // (a manager-based type always yields a snapshot; a
                // manager-less MEP type always exposes routing params) —
                // it is never proof of drift (audit L9: a transient read
                // failure must not mark the family RoutingDrift and let
                // "Обновить" overwrite a healthy routing).
                if (catalogFingerprint is not null && liveFingerprint is null)
                {
                    SmartConLogger.Warn(
                        $"RoutingDrift[{item.Id}]: live routing of '{descriptor.Name}' could not be read — " +
                        "drift not evaluated for this type. [Action: повторите «Проверить»; " +
                        "при повторении ищите причину в Debug-логе экстрактора]");
                    drift[key] = false;
                    continue;
                }
                drift[key] = !string.Equals(catalogFingerprint, liveFingerprint, StringComparison.Ordinal);
            }

            if (drift.Values.Any(v => v))
            {
                SmartConLogger.Info(
                    $"RoutingDrift[{item.Id}]: live routing of {drift.Count(v => v.Value)} type(s) " +
                    "differs from the catalog links");
            }
            return drift;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same degradation rule as the content refinement: a transient
            // failure keeps the marker-based verdict, never aborts the check.
            SmartConLogger.Warn(
                $"RoutingDrift[{item.Id}]: probe failed: {ex.Message} " +
                "[Action: routing-дрейф не проверен для этого семейства; повторите «Проверить»]");
            return null;
        }
    }
}
