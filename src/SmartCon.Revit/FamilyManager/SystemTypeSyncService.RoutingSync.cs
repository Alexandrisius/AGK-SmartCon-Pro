using System.Collections;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using Electrical = Autodesk.Revit.DB.Electrical;

namespace SmartCon.Revit.FamilyManager;

public sealed partial class SystemTypeSyncService
{
    /// <summary>
    /// Parameter-based routing sync of manager-less MEPCurve types
    /// (ADR-072 plan item 2b, FHV19): each <c>"Param:&lt;BIP&gt;"</c>
    /// rule is written as the fitting-symbol parameter value
    /// (<c>InvalidElementId</c> for no-part/"Нет" rules); the preferred
    /// junction of flex types is written to
    /// <c>RBS_CURVETYPE_PREFERRED_BRANCH_PARAM</c> (visible there only).
    /// Fittings are already loaded by Phase A
    /// (<see cref="EnsureFittingDependencies"/>) before the transaction.
    /// </summary>
    private int SyncRoutingParamsFromDb(
        Document activeDoc,
        MEPCurveType target,
        RoutingPreferencesSnapshot routing)
    {
        var notConverged = 0;

        foreach (Parameter candidate in target.Parameters)
        {
            if (!RoutingDrivingParameters.IsPreferredBranch(candidate))
                continue;
            if (candidate.IsReadOnly)
                break;
            try
            {
                if (!candidate.Set(routing.PreferredJunctionType))
                {
                    notConverged++;
                    SmartConLogger.Debug($"PreferredBranch write rejected on '{target.Name}'");
                }
            }
            catch (Exception ex)
            {
                notConverged++;
                SmartConLogger.Debug($"PreferredBranch write skipped: {ex.Message}");
            }
            break;
        }

        foreach (var rule in routing.Rules)
        {
            if (!RoutingGroupKeys.IsParamGroup(rule.GroupKey))
                continue;
            var bipName = RoutingGroupKeys.ParamNameOf(rule.GroupKey);
            if (bipName is null || !Enum.TryParse(bipName, out BuiltInParameter bip))
            {
                notConverged++;
                SmartConLogger.Warn(
                    $"Routing group key '{rule.GroupKey}' is not a known built-in parameter. " +
                    "[Action: rule skipped; reimport the category with the current plugin]");
                continue;
            }

            Parameter? param = null;
            try { param = target.get_Parameter(bip); }
            catch (Exception ex) { SmartConLogger.Debug($"get_Parameter({bipName}) failed: {ex.Message}"); }
            if (param is null || param.IsReadOnly)
            {
                notConverged++;
                SmartConLogger.Warn(
                    $"Routing parameter '{bipName}' is not writable on type '{target.Name}'. " +
                    "[Action: rule skipped; check the type's routing settings in the project]");
                continue;
            }

            var value = ElementId.InvalidElementId;
            if (rule.PartName is not null)
            {
                var separator = rule.PartName.IndexOf(':');
                if (separator <= 0 || separator == rule.PartName.Length - 1)
                {
                    notConverged++;
                    SmartConLogger.Warn(
                        $"Routing part token '{rule.PartName}' ({bipName}) is not 'Family:Type'. " +
                        "[Action: rule skipped; fix the routing in the catalog editor]");
                    continue;
                }
                var symbol = FindFittingSymbol(
                    activeDoc,
                    rule.PartName.Substring(0, separator),
                    rule.PartName.Substring(separator + 1));
                if (symbol is null)
                {
                    notConverged++;
                    SmartConLogger.Warn(
                        $"Routing part '{rule.PartName}' ({bipName}) could not be resolved in the project. " +
                        "[Action: rule skipped; check the dependency warnings above]");
                    continue;
                }
                value = symbol.Id;
            }

            try
            {
                if (!param.Set(value))
                {
                    notConverged++;
                    SmartConLogger.Debug($"Param.Set({bipName}) rejected on '{target.Name}'");
                }
            }
            catch (Exception ex)
            {
                notConverged++;
                SmartConLogger.Debug($"Param.Set({bipName}) failed on '{target.Name}': {ex.Message}");
            }
        }

        return notConverged;
    }

    /// <summary>
    /// Phase A of routing sync: every fitting referenced by the reference
    /// routing rules must exist in the project BEFORE the sync transaction
    /// opens (LoadFamily is forbidden inside a modifiable document).
    /// </summary>
    private void EnsureFittingDependencies(
        Document activeDoc,
        RoutingPreferencesSnapshot routing,
        int targetRevitVersion,
        string parentCatalogItemId)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in routing.Rules)
        {
            // Segments are resolved by the segment synchronizer, never via
            // the catalog fitting path (a segment name could contain ':').
            if (rule.GroupType == (int)RoutingPreferenceRuleGroupType.Segments) continue;
            var partName = rule.PartName;
            if (partName is null) continue;
            var separator = partName.IndexOf(':');
            if (separator <= 0 || separator == partName.Length - 1) continue;
            if (!seen.Add(partName)) continue;

            var familyName = partName.Substring(0, separator);
            var typeName = partName.Substring(separator + 1);
            _fittingResolver.EnsureFitting(activeDoc, familyName, typeName, targetRevitVersion, parentCatalogItemId);
        }
    }

    /// <summary>
    /// Rebuilds the target type's routing preferences from the reference:
    /// segments are synchronized first (their ids feed the Segments rules),
    /// then every reference group is cleared and re-added in reference order,
    /// and target-only groups are cleared (the catalog is always right).
    /// The Segments group is never emptied — a MEP curve type without a
    /// segment rule is invalid in Revit; when no reference segment can be
    /// resolved, the existing rules are kept and reported as not converged.
    /// </summary>
    private int SyncRoutingPreferences(
        Document sourceDoc,
        Document activeDoc,
        MEPCurveType target,
        RoutingPreferencesSnapshot routing)
    {
        var notConverged = 0;
        using var manager = target.RoutingPreferenceManager;

        try
        {
            manager.PreferredJunctionType = (PreferredJunctionType)routing.PreferredJunctionType;
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"PreferredJunctionType write skipped: {ex.Message}");
        }

        // 1) Segments — resolved first, they feed the Segments group rules.
        var segmentIds = new Dictionary<string, ElementId>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in routing.Rules)
        {
            if (rule.GroupType != (int)RoutingPreferenceRuleGroupType.Segments) continue;
            if (rule.PartName is null || segmentIds.ContainsKey(rule.PartName)) continue;

            var segmentResult = _segmentSync.SyncSegment(sourceDoc, activeDoc, rule.PartName);
            notConverged += segmentResult.SizesNotConverged;
            if (segmentResult.SegmentId is not null)
            {
                segmentIds[rule.PartName] = segmentResult.SegmentId;
            }
        }

        var referenceGroups = new HashSet<int>(routing.Rules.Select(r => r.GroupType));
        // Groups that receive a rule via the Transitions retry below — they
        // carry reference intent too, so the step-3 cleanup must not wipe
        // them right after the retry wrote into them.
        var retryWrittenGroups = new HashSet<int>();
        // Deferred multi-shape transition retries (audit M4): applied AFTER
        // the group rebuild loop — writing into group 7/8/9 DURING the
        // Transitions(4) rebuild would be wiped by the subsequent rebuild of
        // that very group whenever the reference also carries rules there
        // (groups are processed in ascending ordinal order, 4 before 7).
        var deferredRetries = new List<(RoutingPreferenceRule Rule, ElementId PartId, string? PartName)>();

        // 2) Rebuild every group present in the reference, in reference order.
        foreach (var group in referenceGroups.OrderBy(g => g))
        {
            var groupType = (RoutingPreferenceRuleGroupType)group;
            var rules = routing.Rules.Where(r => r.GroupType == group).ToList();

            if (groupType == RoutingPreferenceRuleGroupType.Segments)
            {
                var resolvable = rules.Count(r => r.PartName is not null && segmentIds.ContainsKey(r.PartName));
                if (resolvable == 0)
                {
                    notConverged += rules.Count;
                    SmartConLogger.Warn(
                        $"Type '{target.Name}': no reference segment could be resolved; " +
                        "existing Segments rules are kept (a MEP type cannot have an empty Segments group). " +
                        "[Action: check the segment warnings above, fix dependencies, then re-run the sync]");
                    continue;
                }
            }

            for (var i = manager.GetNumberOfRules(groupType) - 1; i >= 0; i--)
            {
                try { manager.RemoveRule(groupType, i); }
                catch (Exception ex)
                {
                    SmartConLogger.Debug($"RemoveRule({groupType}[{i}]) skipped: {ex.Message}");
                }
            }

            foreach (var ruleSnapshot in rules)
            {
                var partId = ResolveRulePart(activeDoc, groupType, ruleSnapshot, segmentIds);
                if (partId is null)
                {
                    // "No part" rules (MEPPartId = InvalidElementId — e.g.
                    // welded systems without fittings) are legal reference
                    // content, not a failure: recreate them as-is.
                    if (ruleSnapshot.PartName is null &&
                        groupType != RoutingPreferenceRuleGroupType.Segments)
                    {
                        partId = ElementId.InvalidElementId;
                    }
                    else
                    {
                        notConverged++;
                        SmartConLogger.Warn(
                            $"Routing rule '{ruleSnapshot.PartName ?? "<none>"}' (group {groupType}) " +
                            "could not be resolved in the project. " +
                            "[Action: the rule was skipped; check the dependency warnings above]");
                        continue;
                    }
                }

                try
                {
                    var rule = new RoutingPreferenceRule(partId, ruleSnapshot.Description ?? string.Empty);
                    foreach (var criterion in ruleSnapshot.Criteria)
                    {
                        // Only pipes carry size ranges in routing (owner
                        // decision 2026-08-30) — legacy duct criteria are
                        // dropped on write, mirroring the extraction.
                        if (criterion.CriterionType == nameof(PrimarySizeCriterion)
                            && target is Autodesk.Revit.DB.Plumbing.PipeType)
                        {
                            rule.AddCriterion(new PrimarySizeCriterion(
                                criterion.MinimumSize, criterion.MaximumSize));
                        }
                    }
                    try
                    {
                        manager.AddRule(groupType, rule);
                    }
                    catch (ArgumentException) when (groupType == RoutingPreferenceRuleGroupType.Transitions)
                    {
                        // Revit rejects multi-shape transition fittings in
                        // the plain Transitions group ("The rule cannot be
                        // added to the groupType") — projects may still
                        // carry such rules there. Defer the retry into the
                        // shape-specific transition groups until after the
                        // rebuild loop (audit M4).
                        deferredRetries.Add((rule, partId, ruleSnapshot.PartName));
                    }
                }
                catch (Exception ex)
                {
                    notConverged++;
                    SmartConLogger.Debug(
                        $"AddRule({groupType}, '{ruleSnapshot.PartName}') failed: {ex.Message}");
                }
            }
        }

        // Deferred multi-shape transition retries (audit M4/L20): try the
        // shape-specific groups in turn — the fitting's connector profiles
        // decide which one accepts it (validator 2026-08-30: drop a same-part
        // rule first — a re-sync must not stack duplicates).
        foreach (var (retryRule, retryPartId, retryPartName) in deferredRetries)
        {
            var applied = false;
            foreach (var retryGroup in new[]
            {
                RoutingPreferenceRuleGroupType.TransitionsRectangularToRound,
                RoutingPreferenceRuleGroupType.TransitionsRectangularToOval,
                RoutingPreferenceRuleGroupType.TransitionsOvalToRound,
            })
            {
                try
                {
                    for (var i = manager.GetNumberOfRules(retryGroup) - 1; i >= 0; i--)
                    {
                        try
                        {
                            using var existing = manager.GetRule(retryGroup, i);
                            if (existing.MEPPartId == retryPartId)
                                manager.RemoveRule(retryGroup, i);
                        }
                        catch { /* best effort — AddRule below still reports */ }
                    }
                    manager.AddRule(retryGroup, retryRule);
                    retryWrittenGroups.Add((int)retryGroup);
                    applied = true;
                    break;
                }
                catch (ArgumentException)
                {
                    // The fitting's profile does not match this shape
                    // group — try the next one.
                }
            }
            if (!applied)
            {
                notConverged++;
                SmartConLogger.Warn(
                    $"Routing rule '{retryPartName ?? "<none>"}' (multi-shape transition) could not be added " +
                    "to any transition group. [Action: правило пропущено; проверьте семейство перехода — " +
                    "его коннекторы должны быть мульти-форменными]");
            }
        }

        // 3) Clear target-only groups (except Segments — handled above).
        foreach (RoutingPreferenceRuleGroupType groupType in Enum.GetValues(typeof(RoutingPreferenceRuleGroupType)))
        {
            if (groupType == RoutingPreferenceRuleGroupType.Undefined) continue;
            if (groupType == RoutingPreferenceRuleGroupType.Segments) continue;
            if (referenceGroups.Contains((int)groupType)) continue;
            if (retryWrittenGroups.Contains((int)groupType)) continue;

            for (var i = manager.GetNumberOfRules(groupType) - 1; i >= 0; i--)
            {
                try { manager.RemoveRule(groupType, i); }
                catch (Exception ex)
                {
                    SmartConLogger.Debug($"RemoveRule({groupType}[{i}]) skipped: {ex.Message}");
                }
            }
        }

        return notConverged;
    }

    private ElementId? ResolveRulePart(
        Document activeDoc,
        RoutingPreferenceRuleGroupType groupType,
        RoutingRuleSnapshot rule,
        IReadOnlyDictionary<string, ElementId> segmentIds)
    {
        if (rule.PartName is null) return null;

        if (groupType == RoutingPreferenceRuleGroupType.Segments)
        {
            return segmentIds.TryGetValue(rule.PartName, out var segmentId) ? segmentId : null;
        }

        var separator = rule.PartName.IndexOf(':');
        if (separator <= 0 || separator == rule.PartName.Length - 1) return null;

        var familyName = rule.PartName.Substring(0, separator);
        var typeName = rule.PartName.Substring(separator + 1);
        return FindFittingSymbol(activeDoc, familyName, typeName)?.Id;
    }

    private static FamilySymbol? FindFittingSymbol(Document doc, string familyName, string typeName)
    {
        using var collector = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol));
        foreach (var symbol in collector.Cast<FamilySymbol>())
        {
            if (!string.Equals(symbol.Name, typeName, StringComparison.OrdinalIgnoreCase)) continue;
            var symbolFamilyName = symbol.Family?.Name ?? symbol.FamilyName;
            if (string.Equals(symbolFamilyName, familyName, StringComparison.OrdinalIgnoreCase))
            {
                return symbol;
            }
        }
        return null;
    }

    private static int? GetCategoryOrdinal(ElementType? type)
    {
        try
        {
            var builtIn = Compatibility.CategoryCompat.GetBuiltInCategory(type?.Category);
            if (builtIn == BuiltInCategory.INVALID) return null;
            return (int)builtIn;
        }
        catch
        {
            return null;
        }
    }

    private static ElementType? FindPrototypeType(Document doc, int? categoryOrdinal, string? familyName, string? familyKey)
    {
        // Without a category we must not create: duplicating a type of a
        // foreign category would produce a wrong-category type. Creation is
        // only possible with a known category filter.
        if (!categoryOrdinal.HasValue) return null;

        try
        {
            using var collector = new FilteredElementCollector(doc)
                .OfClass(typeof(ElementType))
                .OfCategoryId(Core.Compatibility.ElementIdCompat.Create(categoryOrdinal.Value));
            // #183: the prototype must belong to the SAME system family —
            // duplicating a foreign-family type would register the new type
            // in the wrong family (e.g. "Conduit with Fittings" instead of
            // "Conduit without Fittings"). A null family name keeps the
            // legacy any-prototype behaviour (should not happen in practice —
            // every system ElementType reports a FamilyName).
            // #190 (ADR-064): the locale-invariant key is the primary filter.
            // Same collision class as the finder (manual test 2026-08-04):
            // a WireMaterialType settings object shares name/family/category
            // with the real WireType — it must never become the Duplicate
            // prototype on the create path either (2026+: the Conductor*
            // replacements are covered by the same filter, #233).
            var isPhantom = RevitSystemTypeFinder.CreatePhantomFilter(doc);
            return collector.Cast<ElementType>()
                .Where(t => !isPhantom(t))
                .FirstOrDefault(t =>
                !string.IsNullOrEmpty(familyKey)
                    ? string.Equals(SystemFamilyKeyResolver.Resolve(t), familyKey, StringComparison.OrdinalIgnoreCase)
                    : string.IsNullOrEmpty(familyName)
                        || string.Equals(t.FamilyName, familyName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }
}
