using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Implementation;

/// <summary>
/// Bidirectional mapping between the extraction-time routing model
/// (<see cref="SystemTypeSnapshot.Routing"/>) and the V34 storage records
/// (<see cref="FamilyRoutingRuleInfo"/> / <see cref="FamilyRoutingTypeSettings"/>)
/// — ADR-072. One type maps to N rule rows + 1 settings row (when the type
/// has routing at all); group keys are the storage identity
/// (<see cref="RoutingGroupKeys"/>).
/// </summary>
public static class RoutingRuleRecordMapper
{
    /// <summary>
    /// Snapshot → storage records. <paramref name="rules"/> receives one
    /// row per rule with per-group zero-based order; <paramref name="settings"/>
    /// receives the per-type scalar row. Both stay empty when the type has
    /// no routing (<c>null</c> snapshot — canonical "not a routed type").
    /// </summary>
    public static void ToRecords(
        SystemTypeSnapshot type,
        List<FamilyRoutingRuleInfo> rules,
        List<FamilyRoutingTypeSettings> settings)
    {
        if (type.Routing is null)
            return;

        var familyKey = type.FamilyKey ?? string.Empty;
        settings.Add(new FamilyRoutingTypeSettings(
            type.Name, familyKey, type.Routing.PreferredJunctionType));

        var groupOrders = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var rule in type.Routing.Rules)
        {
            var groupKey = rule.GroupType == RoutingGroupKeys.ParamGroupType
                ? rule.GroupKey ?? RoutingGroupKeys.ParamPrefix
                : RoutingGroupKeys.ForManagerGroup(rule.GroupType);
            groupOrders.TryGetValue(groupKey, out var order);
            groupOrders[groupKey] = order + 1;

            rules.Add(new FamilyRoutingRuleInfo(
                type.Name,
                familyKey,
                groupKey,
                order,
                rule.PartName,
                rule.Description,
                rule.Criteria));
        }
    }

    /// <summary>
    /// Storage records → snapshot. Rules of the type are filtered by
    /// (familyKey, typeName), manager groups ordered by group ordinal then
    /// rule order, parameter groups after them by key then rule order —
    /// the canonical ROUTING-section order of the extractor.
    /// <c>null</c> when the type has no settings row (not a routed type).
    /// </summary>
    public static RoutingPreferencesSnapshot? ToSnapshot(
        string typeName,
        string familyKey,
        IReadOnlyList<FamilyRoutingRuleInfo> rules,
        IReadOnlyList<FamilyRoutingTypeSettings> settings)
    {
        var typeSettings = settings.FirstOrDefault(s =>
            string.Equals(s.TypeName, typeName, StringComparison.Ordinal)
            && string.Equals(s.FamilyKey, familyKey, StringComparison.Ordinal));
        if (typeSettings is null)
            return null;

        var typeRules = rules
            .Where(r =>
                string.Equals(r.TypeName, typeName, StringComparison.Ordinal)
                && string.Equals(r.FamilyKey, familyKey, StringComparison.Ordinal))
            .OrderBy(r => GroupSortKey(r.GroupKey), System.Collections.Generic.Comparer<int>.Default)
            .ThenBy(r => r.GroupKey, StringComparer.Ordinal)
            .ThenBy(r => r.RuleOrder)
            .Select(r =>
            {
                var isParam = RoutingGroupKeys.IsParamGroup(r.GroupKey);
                int? groupType = isParam
                    ? RoutingGroupKeys.ParamGroupType
                    : RoutingGroupKeys.ManagerGroupTypeOf(r.GroupKey);
                if (groupType is null)
                {
                    // Unknown group key (not a known name, not the
                    // "Group{n}" fallback) — skipping beats silently
                    // degrading the rule into Segments(0) (audit M3).
                    Logging.SmartConLogger.Warn(
                        $"Routing group key '{r.GroupKey}' is not recognized — rule " +
                        $"'{r.PartName ?? "<none>"}' skipped on snapshot rebuild. " +
                        "[Action: реимпортируйте эталон текущим плагином, чтобы пересоздать ключи групп]");
                    return null;
                }
                return new RoutingRuleSnapshot(
                    groupType.Value,
                    r.PartName,
                    r.Description,
                    r.Criteria,
                    GroupKey: isParam ? r.GroupKey : null);
            })
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList();

        return new RoutingPreferencesSnapshot(typeSettings.PreferredJunctionType, typeRules);
    }

    private static int GroupSortKey(string groupKey)
        => RoutingGroupKeys.IsParamGroup(groupKey)
            ? 100 // parameter groups sort after manager groups (canonical order)
            : RoutingGroupKeys.ManagerGroupTypeOf(groupKey) ?? 99;
}
