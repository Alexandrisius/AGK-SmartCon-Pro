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

    public RoutingDriftProbe(
        IFamilyRoutingRuleRepository routingRules,
        IFamilySnapshotExtractor extractor,
        ISystemTypeFinder typeFinder,
        IFamilyCatalogProvider catalog)
    {
        _routingRules = routingRules ?? throw new ArgumentNullException(nameof(routingRules));
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _typeFinder = typeFinder ?? throw new ArgumentNullException(nameof(typeFinder));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
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
            if (!AsyncBridge.RunSync(() => _routingRules.HasAnyForItemAsync(catalogItemId)))
                return null;

            var (rules, settings) = AsyncBridge.RunSync(
                () => _routingRules.ReadForItemAsync(catalogItemId));
            var setting = settings.FirstOrDefault(s =>
                string.Equals(s.TypeName, typeName, StringComparison.Ordinal)
                && (familyKey is null || string.Equals(s.FamilyKey, familyKey, StringComparison.Ordinal)));
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
