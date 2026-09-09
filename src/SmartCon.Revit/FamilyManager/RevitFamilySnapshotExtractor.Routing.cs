using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Util;

namespace SmartCon.Revit.FamilyManager;

public sealed partial class RevitFamilySnapshotExtractor
{
    /// <summary>
    /// Routing preferences of a MEP curve type — pipe, duct, cable tray,
    /// conduit (ADR-056). <c>null</c> for non-MEP types. Rules keep
    /// manager order (first matching rule wins — order is content).
    /// Resolved part names are family-qualified for fitting symbols
    /// (<c>"{Family}:{Type}"</c>) so two fittings sharing a type name
    /// cannot collide.
    /// </summary>
    private static RoutingPreferencesSnapshot? ExtractRoutingPreferences(
        ElementType elementType, Document doc)
    {
        if (elementType is not MEPCurveType mepCurveType)
            return null;

        try
        {
            using var manager = mepCurveType.RoutingPreferenceManager;
            if (manager is null)
            {
                // FHV19 (ADR-072): flex/conduit/cable-tray types have no
                // RoutingPreferenceManager (probe RoutingStorageReality
                // 2026-08-29) — their fitting selection lives in visible
                // built-in parameters.
                return ExtractParamBasedRouting(elementType, doc);
            }

            var rules = new List<RoutingRuleSnapshot>();
            // Only PIPES define size ranges in their routing rules (owner
            // decision 2026-08-30): duct manager rules still report a
            // default PrimarySizeCriterion, but duct size availability is
            // configured elsewhere — the criterion must not become routing
            // content (phantom size UI + drift against the editor, which
            // keeps no size conditions for non-pipes).
            var includeSizeCriteria = elementType is PipeType;
            foreach (RoutingPreferenceRuleGroupType group in Enum.GetValues(typeof(RoutingPreferenceRuleGroupType)))
            {
                if (group == RoutingPreferenceRuleGroupType.Undefined)
                    continue;

                int ruleCount = manager.GetNumberOfRules(group);
                for (var i = 0; i < ruleCount; i++)
                {
                    RoutingPreferenceRule rule;
                    try
                    {
                        rule = manager.GetRule(group, i);
                    }
                    catch (Exception ex)
                    {
                        SmartConLogger.Debug(
                            $"Routing rule read failed ({group}[{i}]) for type '{elementType.Name}': {ex.Message}");
                        continue;
                    }

                    rules.Add(ConvertRoutingRule(rule, group, doc, includeSizeCriteria));
                }
            }

            return new RoutingPreferencesSnapshot(
                PreferredJunctionType: (int)manager.PreferredJunctionType,
                Rules: rules);
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"RoutingPreferences read failed for type '{elementType.Name}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Parameter-based routing of manager-less MEPCurve types — flex
    /// pipe/duct, conduit, cable tray (FHV19, ADR-072). Each visible
    /// routing-driving built-in parameter becomes one rule in a
    /// <c>"Param:&lt;BIP&gt;"</c> group (deterministic key order);
    /// <c>RBS_CURVETYPE_PREFERRED_BRANCH_PARAM</c> maps to
    /// <see cref="RoutingPreferencesSnapshot.PreferredJunctionType"/>.
    /// NOTE: the parameter's int convention is INVERTED against the
    /// <c>PreferredJunctionType</c> enum (param: 0=Tap, 1=Tee — Autodesk
    /// DevBlog; enum: Tee=0, Tap=1 — revitapidocs). The RAW value is stored
    /// so extract→DB→sync round-trips stably; the routing editor translates
    /// it per category for display (audit H3).
    /// <c>null</c> when the type exposes no routing-driving parameters at
    /// all (canonical "not routed" state).
    /// </summary>
    private static RoutingPreferencesSnapshot? ExtractParamBasedRouting(
        ElementType elementType, Document doc)
    {
        var keyed = new List<KeyValuePair<string, RoutingRuleSnapshot>>();
        var preferredJunction = 0;
        var foundAny = false;

        foreach (Parameter param in elementType.Parameters)
        {
            if (RoutingDrivingParameters.IsPreferredBranch(param))
            {
                foundAny = true;
                try
                {
                    if (param.HasValue)
                        preferredJunction = param.AsInteger();
                }
                catch (Exception ex)
                {
                    SmartConLogger.Debug(
                        $"PreferredBranch read failed for type '{elementType.Name}': {ex.Message}");
                }
                continue;
            }

            var bip = RoutingDrivingParameters.TryGetRoutingParam(param);
            if (bip is null)
                continue;
            foundAny = true;

            string? partName = null;
            try
            {
                if (param.HasValue)
                {
                    var partId = param.AsElementId();
                    if (partId is not null && partId != ElementId.InvalidElementId)
                    {
                        var element = doc.GetElement(partId);
                        // Audit L21: a non-invalid id resolving to NOTHING is
                        // a stale reference (deleted fitting), not a
                        // deliberate «Нет» — mask it and the catalog loses
                        // the distinction (and the presence flag) silently.
                        if (element is null)
                        {
                            SmartConLogger.Warn(
                                $"Param routing rule ({bip}) of type '{elementType.Name}' holds a stale " +
                                $"ElementId {partId} — recorded as «Нет». [Action: переназначьте деталь " +
                                "в свойствах типа в Revit или во вкладке «Трассировка»]");
                        }
                        partName = element switch
                        {
                            FamilySymbol symbol => $"{symbol.Family?.Name}:{symbol.Name}",
                            _ => element?.Name,
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                SmartConLogger.Debug(
                    $"Param routing rule read failed ({bip}) for type '{elementType.Name}': {ex.Message}");
                partName = null;
            }

            var groupKey = RoutingDrivingParameters.GroupKey(bip.Value);
            keyed.Add(new KeyValuePair<string, RoutingRuleSnapshot>(
                groupKey,
                new RoutingRuleSnapshot(
                    RoutingGroupKeys.ParamGroupType,
                    partName,
                    string.Empty,
                    Array.Empty<RoutingCriterionSnapshot>(),
                    GroupKey: groupKey)));
        }

        if (!foundAny)
            return null;

        var rules = keyed
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Value)
            .ToList();
        return new RoutingPreferencesSnapshot(preferredJunction, rules);
    }

    private static RoutingRuleSnapshot ConvertRoutingRule(
        RoutingPreferenceRule rule, RoutingPreferenceRuleGroupType group, Document doc, bool includeSizeCriteria)
    {
        string? partName = null;
        try
        {
            if (rule.MEPPartId is not null && rule.MEPPartId != ElementId.InvalidElementId)
            {
                var element = doc.GetElement(rule.MEPPartId);
                partName = element switch
                {
                    FamilySymbol symbol => $"{symbol.Family?.Name}:{symbol.Name}",
                    _ => element?.Name,
                };
            }
        }
        catch
        {
            partName = null;
        }

        var criteria = new List<RoutingCriterionSnapshot>();
        try
        {
            var criteriaCount = rule.NumberOfCriteria;
            for (var i = 0; i < criteriaCount; i++)
            {
                var criterion = rule.GetCriterion(i);
                switch (criterion)
                {
                    case PrimarySizeCriterion sizeCriterion when includeSizeCriteria:
                        criteria.Add(new RoutingCriterionSnapshot(
                            nameof(PrimarySizeCriterion),
                            sizeCriterion.MinimumSize,
                            sizeCriterion.MaximumSize));
                        break;
                    case PrimarySizeCriterion:
                        break;
                    case not null:
                        criteria.Add(new RoutingCriterionSnapshot(
                            criterion.GetType().Name, 0, 0));
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"Routing criteria read failed: {ex.Message}");
        }

        string description;
        try
        {
            description = rule.Description ?? string.Empty;
        }
        catch
        {
            description = string.Empty;
        }

        return new RoutingRuleSnapshot(
            GroupType: (int)group,
            PartName: partName,
            Description: description,
            Criteria: criteria);
    }

    /// <summary>
    /// Segment size tables referenced by the type's routing rules
    /// (Segments group) — FHV4 hash content (#179, ADR-065). Mirrors
    /// <c>RevitSegmentSyncService.ReadSegment</c>; <c>null</c> for
    /// non-MEP types and types without segment rules.
    /// </summary>
    private static IReadOnlyList<SegmentSnapshot>? ExtractSegments(
        ElementType elementType, Document doc)
    {
        if (elementType is not MEPCurveType mepCurveType)
            return null;

        try
        {
            using var manager = mepCurveType.RoutingPreferenceManager;
            if (manager is null)
                return null;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var segments = new List<SegmentSnapshot>();
            var ruleCount = manager.GetNumberOfRules(RoutingPreferenceRuleGroupType.Segments);
            for (var i = 0; i < ruleCount; i++)
            {
                RoutingPreferenceRule rule;
                try
                {
                    rule = manager.GetRule(RoutingPreferenceRuleGroupType.Segments, i);
                }
                catch (Exception ex)
                {
                    SmartConLogger.Debug(
                        $"Segment rule read failed (Segments[{i}]) for type '{elementType.Name}': {ex.Message}");
                    continue;
                }

                if (rule.MEPPartId is null || rule.MEPPartId == ElementId.InvalidElementId)
                    continue;
                if (doc.GetElement(rule.MEPPartId) is not Segment segment)
                    continue;
                if (!seen.Add(segment.Name))
                    continue;

                // FHV21 (owner decision 2026-09-01): the rule's size-range
                // criterion (Мин/Макс in the routing dialog) is part of the
                // segment configuration — it enters the SEGMENTS hash
                // section and the per-version segment-rule store.
                double? ruleMin = null;
                double? ruleMax = null;
                try
                {
                    for (var c = 0; c < rule.NumberOfCriteria; c++)
                    {
                        if (rule.GetCriterion(c) is PrimarySizeCriterion sizeCriterion)
                        {
                            ruleMin = sizeCriterion.MinimumSize;
                            ruleMax = sizeCriterion.MaximumSize;
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    SmartConLogger.Debug(
                        $"Segment rule criterion read failed for '{segment.Name}': {ex.Message}");
                }

                segments.Add(BuildSegmentSnapshot(segment, doc) with
                {
                    RuleMinSizeFeet = ruleMin,
                    RuleMaxSizeFeet = ruleMax,
                });
            }

            return segments.Count == 0 ? null : segments;
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"Segments read failed for type '{elementType.Name}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Shared segment snapshot builder (FHV4, ADR-065) — single source for
    /// the hash path (extractor) and the sync path
    /// (<c>RevitSegmentSyncService.ReadSegment</c>), so the hash always
    /// reflects exactly what the sync writes.
    /// </summary>
    internal static SegmentSnapshot BuildSegmentSnapshot(Segment segment, Document doc)
    {
        string? materialName = null;
        try
        {
            if (segment.MaterialId is not null && segment.MaterialId != ElementId.InvalidElementId)
            {
                materialName = doc.GetElement(segment.MaterialId)?.Name;
            }
        }
        catch { /* unresolved material — null */ }

        string? scheduleName = null;
        if (segment is PipeSegment pipeSegment)
        {
            try
            {
                if (pipeSegment.ScheduleTypeId is not null &&
                    pipeSegment.ScheduleTypeId != ElementId.InvalidElementId)
                {
                    scheduleName = doc.GetElement(pipeSegment.ScheduleTypeId)?.Name;
                }
            }
            catch { /* unresolved schedule — null */ }
        }

        var sizes = segment.GetSizes()
            .Select(s => new SegmentSizeSnapshot(
                s.NominalDiameter, s.InnerDiameter, s.OuterDiameter,
                s.UsedInSizeLists, s.UsedInSizing))
            .ToList();

        return new SegmentSnapshot(segment.Name, materialName, scheduleName, segment.Roughness, sizes);
    }
}
