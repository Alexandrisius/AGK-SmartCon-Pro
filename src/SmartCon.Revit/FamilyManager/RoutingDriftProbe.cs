using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Threading;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// ADR-072 World B placement probe: answers whether the LIVE routing
/// preferences of the project's type differ from the catalog's item-level
/// routing links (<see cref="RoutingFingerprint"/>). <c>null</c> = the
/// catalog has no opinion (non-MEP category, no item-level links, the type
/// is not in the project yet, transient failure) — placement proceeds
/// without the overwrite prompt.
/// </summary>
public sealed class RoutingDriftProbe
{
    private readonly IFamilyRoutingRuleRepository _routingRules;
    private readonly IFamilySnapshotExtractor _extractor;
    private readonly ISystemTypeFinder _typeFinder;
    private readonly IFamilyCatalogProvider _catalog;
    private readonly ISegmentRuleRepository? _segmentRules;

    public RoutingDriftProbe(
        IFamilyRoutingRuleRepository routingRules,
        IFamilySnapshotExtractor extractor,
        ISystemTypeFinder typeFinder,
        IFamilyCatalogProvider catalog,
        ISegmentRuleRepository? segmentRules = null)
    {
        _routingRules = routingRules ?? throw new ArgumentNullException(nameof(routingRules));
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _typeFinder = typeFinder ?? throw new ArgumentNullException(nameof(typeFinder));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _segmentRules = segmentRules;
    }

    /// <summary>Main Revit thread only (reads live elements).</summary>
    public bool? HasRoutingDrift(
        Document doc,
        string catalogItemId,
        string typeName,
        string? familyName = null,
        string? familyKey = null)
    {
        try
        {
            var item = AsyncBridge.RunSync(
                () => _catalog.GetItemAsync(catalogItemId, CancellationToken.None));
            if (item is null || !RoutingGroupCatalog.IsMepCurveCategory(item.RevitCategoryId))
                return null;

            // Same source chain as sync (audit M9): item-level V37 first,
            // the current version's V34 rows as the legacy fallback —
            // otherwise an unhealed legacy window leaves the probe blind
            // while sync still applies the V34 routing.
            var (rules, settings) = AsyncBridge.RunSync(async () =>
            {
                if (await _routingRules.HasAnyForItemAsync(catalogItemId).ConfigureAwait(false))
                    return await _routingRules.ReadForItemAsync(catalogItemId).ConfigureAwait(false);
                if (!await _routingRules.HasRulesForCurrentVersionAsync(catalogItemId).ConfigureAwait(false))
                    return default;
                return await _routingRules.ReadForCurrentVersionAsync(catalogItemId).ConfigureAwait(false);
            });
            if (rules is null)
                return null;

            // FHV21: segment rules compose from the per-version store of the
            // CURRENT version — the drift fingerprint then matches what sync
            // would actually apply (fittings item-level, segments versioned).
            if (_segmentRules is not null)
            {
                var perVersionSegments = AsyncBridge.RunSync(
                    () => _segmentRules.ReadForCurrentVersionAsync(catalogItemId));
                rules = SegmentRuleComposition.Compose(rules, perVersionSegments);
            }

            var setting = settings.FirstOrDefault(s =>
                string.Equals(s.TypeName, typeName, StringComparison.Ordinal)
                // Audit L10: a legacy row with an EMPTY FamilyKey must still
                // match (the key could not be resolved at storage time).
                && (string.IsNullOrEmpty(s.FamilyKey) || familyKey is null
                    || string.Equals(s.FamilyKey, familyKey, StringComparison.Ordinal)));
            if (setting is null)
                return null;

            // Only pipes carry size ranges in routing (owner decision
            // 2026-08-30) — normalize legacy DB criteria away for the
            // other MEPCurve categories (RoutingFingerprint docs).
            var includeSizeCriteria = RoutingGroupCatalog.HasSizeCriteria(item.RevitCategoryId);
            RoutingPreferencesSnapshot? Normalize(RoutingPreferencesSnapshot? snapshot)
                => RoutingFingerprint.WithCanonicalTransitionGroups(
                    includeSizeCriteria ? snapshot : RoutingFingerprint.WithoutSizeCriteria(snapshot));

            var catalogFingerprint = RoutingFingerprint.Compute(
                Normalize(RoutingRuleRecordMapper.ToSnapshot(setting.TypeName, setting.FamilyKey, rules, settings)));

            var typeId = _typeFinder.FindTypeByName(doc, typeName, item.RevitCategoryId, familyName, familyKey);
            if (typeId is null)
            {
                // The type is not in the project yet — nothing to overwrite;
                // the catalog routing applies as brand-new settings.
                return null;
            }

            var liveFingerprint = RoutingFingerprint.Compute(
                Normalize(_extractor.ExtractSystemTypeRouting(doc, typeId)));
            // Live-null on an MEP type means the routing READ failed (audit
            // L9) — it is never proof of drift; placement proceeds without
            // the prompt rather than risking a wrong overwrite warning.
            if (catalogFingerprint is not null && liveFingerprint is null)
            {
                SmartConLogger.Warn(
                    $"RoutingDriftProbe: live routing of '{typeName}' could not be read — " +
                    "drift not evaluated. [Action: размещение продолжится без подтверждения; " +
                    "при повторении ищите причину в Debug-логе экстрактора]");
                return null;
            }
            var drift = !string.Equals(catalogFingerprint, liveFingerprint, StringComparison.Ordinal);
            if (drift)
            {
                SmartConLogger.Info(
                    $"RoutingDriftProbe: type '{typeName}' of item '{catalogItemId}' carries live routing " +
                    "different from the catalog links");
            }
            return drift;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SmartConLogger.Warn(
                $"RoutingDriftProbe failed for '{typeName}' ({catalogItemId}): {ex.Message} " +
                "[Action: размещение продолжится без подтверждения перезаписи трассировки]");
            return null;
        }
    }
}
