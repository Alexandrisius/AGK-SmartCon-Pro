using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Revit implementation of <see cref="ISystemTypeSyncService"/> (Issue #104).
/// Never copies elements between documents: the reference data of the source
/// type (snapshot) is read from the open mini-project and written into the
/// target type of the active project. Missing types are created by
/// duplicating an existing prototype type of the same category.
/// </summary>
public sealed class SystemTypeSyncService : ISystemTypeSyncService
{
    private readonly ITransactionService _tx;
    private readonly IFamilySnapshotExtractor _snapshotExtractor;
    private readonly ISystemTypeFinder _typeFinder;
    private readonly IClock _clock;
    private readonly IMaterialSyncService _materialSync;
    private readonly ISegmentSyncService _segmentSync;
    private readonly IFittingDependencyResolver _fittingResolver;

    public SystemTypeSyncService(
        ITransactionService tx,
        IFamilySnapshotExtractor snapshotExtractor,
        ISystemTypeFinder typeFinder,
        IClock clock,
        IMaterialSyncService materialSync,
        ISegmentSyncService segmentSync,
        IFittingDependencyResolver fittingResolver)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(snapshotExtractor);
        ArgumentNullException.ThrowIfNull(typeFinder);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(materialSync);
        ArgumentNullException.ThrowIfNull(segmentSync);
        ArgumentNullException.ThrowIfNull(fittingResolver);
#else
        if (tx is null) throw new ArgumentNullException(nameof(tx));
        if (snapshotExtractor is null) throw new ArgumentNullException(nameof(snapshotExtractor));
        if (typeFinder is null) throw new ArgumentNullException(nameof(typeFinder));
        if (clock is null) throw new ArgumentNullException(nameof(clock));
        if (materialSync is null) throw new ArgumentNullException(nameof(materialSync));
        if (segmentSync is null) throw new ArgumentNullException(nameof(segmentSync));
        if (fittingResolver is null) throw new ArgumentNullException(nameof(fittingResolver));
#endif
        _tx = tx;
        _snapshotExtractor = snapshotExtractor;
        _typeFinder = typeFinder;
        _clock = clock;
        _materialSync = materialSync;
        _segmentSync = segmentSync;
        _fittingResolver = fittingResolver;
    }

    public SystemTypeSyncResult SyncTypeFromSource(
        Document sourceDoc,
        Document activeDoc,
        string typeName,
        string catalogItemId,
        string versionLabel,
        int sourceRevitVersion)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(sourceDoc);
        ArgumentNullException.ThrowIfNull(activeDoc);
        ArgumentNullException.ThrowIfNull(typeName);
        ArgumentNullException.ThrowIfNull(catalogItemId);
#else
        if (sourceDoc is null) throw new ArgumentNullException(nameof(sourceDoc));
        if (activeDoc is null) throw new ArgumentNullException(nameof(activeDoc));
        if (typeName is null) throw new ArgumentNullException(nameof(typeName));
        if (catalogItemId is null) throw new ArgumentNullException(nameof(catalogItemId));
#endif

        using var _scope = SmartConLogger.BeginScope(
            "SystemSync",
            ("Method", nameof(SyncTypeFromSource)),
            ("TypeName", typeName),
            ("CatalogItemId", catalogItemId));

        var sourceTypeId = _typeFinder.FindTypeByName(sourceDoc, typeName, null);
        if (sourceTypeId is null)
        {
            SmartConLogger.Warn(
                $"Type '{typeName}' not found in the source mini-project. " +
                "[Action: reimport the mini-project into the catalog — its type list is out of sync]");
            return new SystemTypeSyncResult(
                typeName, SystemTypeSyncStatus.NotFoundInSource, 0, 0,
                "Type not found in the source mini-project");
        }

        var sourceType = sourceDoc.GetElement(sourceTypeId) as ElementType;
        var categoryOrdinal = GetCategoryOrdinal(sourceType);
        var template = _snapshotExtractor.ExtractSingleSystemType(sourceDoc, sourceTypeId);

        // Phase A — fitting dependencies. LoadFamily throws when the target
        // document is modifiable, so catalog loads happen BEFORE the sync
        // transaction opens. Consequence: when the sync transaction later
        // rolls back, an already-loaded fitting stays in the project without
        // a routing rule pointing to it. This is acceptable — LoadFamily
        // cannot be rolled back by design, the fitting is a regular catalog
        // family with its own stale lifecycle, and the next sync reuses it.
        if (template.Routing is not null)
        {
            EnsureFittingDependencies(activeDoc, template.Routing, sourceRevitVersion);
        }

        SystemTypeSyncResult? result = null;
        var committed = _tx.RunInTransaction(activeDoc, $"SmartCon: Sync system type '{typeName}'", doc =>
        {
            var targetId = _typeFinder.FindTypeByName(doc, typeName, categoryOrdinal);
            var target = targetId is not null ? doc.GetElement(targetId) as ElementType : null;

            var status = SystemTypeSyncStatus.Updated;
            if (target is null)
            {
                var prototype = FindPrototypeType(doc, categoryOrdinal);
                if (prototype is null)
                {
                    SmartConLogger.Warn(
                        $"Type '{typeName}': no prototype type of category ordinal " +
                        $"{(categoryOrdinal?.ToString() ?? "<none>")} exists in the project; " +
                        "cannot create the type. " +
                        "[Action: create any type of this category in the project manually, then retry]");
                    result = new SystemTypeSyncResult(
                        typeName, SystemTypeSyncStatus.NoPrototypeType, 0, 0,
                        "No prototype type of the same category in the project");
                    return;
                }

                target = prototype.Duplicate(typeName);
                status = SystemTypeSyncStatus.Created;
            }

            var elementIdCache = new Dictionary<string, ElementId?>(StringComparer.Ordinal);
            var (written, skipped) = WriteParameters(sourceDoc, doc, target, template, elementIdCache);

            var notConverged = 0;
            if (template.Routing is not null && target is MEPCurveType mepCurveType)
            {
                notConverged = SyncRoutingPreferences(sourceDoc, doc, mepCurveType, template.Routing);
            }

            RevitFamilyVersionStore.WriteEntityToElement(target, new FamilyVersion(
                SchemaVersion: FamilyVersion.CurrentSchemaVersion,
                CatalogItemId: catalogItemId,
                VersionLabel: versionLabel,
                LoadedAtUtc: _clock.UtcNow,
                SourceRevitVersion: sourceRevitVersion));

            result = new SystemTypeSyncResult(
                typeName, status, written, skipped, NotConvergedCount: notConverged);
        });

        if (!committed || result is null)
        {
            SmartConLogger.Warn(
                $"Type '{typeName}': sync transaction failed. " +
                "[Action: type skipped, batch continues; check the log for the transaction error]");
            return result ?? new SystemTypeSyncResult(
                typeName, SystemTypeSyncStatus.Failed, 0, 0,
                "Transaction failed");
        }

        SmartConLogger.Info(
            $"Type '{typeName}': {result.Status}, {result.ParametersWritten} parameters written, " +
            $"{result.ParametersSkipped} skipped, not converged: {result.NotConvergedCount}.");
        return result;
    }

    /// <summary>
    /// Phase A of routing sync: every fitting referenced by the reference
    /// routing rules must exist in the project BEFORE the sync transaction
    /// opens (LoadFamily is forbidden inside a modifiable document).
    /// </summary>
    private void EnsureFittingDependencies(
        Document activeDoc,
        RoutingPreferencesSnapshot routing,
        int targetRevitVersion)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in routing.Rules)
        {
            var partName = rule.PartName;
            if (partName is null) continue;
            var separator = partName.IndexOf(':');
            if (separator <= 0 || separator == partName.Length - 1) continue;
            if (!seen.Add(partName)) continue;

            var familyName = partName.Substring(0, separator);
            var typeName = partName.Substring(separator + 1);
            _fittingResolver.EnsureFitting(activeDoc, familyName, typeName, targetRevitVersion);
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
                        if (criterion.CriterionType == nameof(PrimarySizeCriterion))
                        {
                            rule.AddCriterion(new PrimarySizeCriterion(
                                criterion.MinimumSize, criterion.MaximumSize));
                        }
                    }
                    manager.AddRule(groupType, rule);
                }
                catch (Exception ex)
                {
                    notConverged++;
                    SmartConLogger.Debug(
                        $"AddRule({groupType}, '{ruleSnapshot.PartName}') failed: {ex.Message}");
                }
            }
        }

        // 3) Clear target-only groups (except Segments — handled above).
        foreach (RoutingPreferenceRuleGroupType groupType in Enum.GetValues(typeof(RoutingPreferenceRuleGroupType)))
        {
            if (groupType == RoutingPreferenceRuleGroupType.Undefined) continue;
            if (groupType == RoutingPreferenceRuleGroupType.Segments) continue;
            if (referenceGroups.Contains((int)groupType)) continue;

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
            var builtIn = Core.Compatibility.CategoryCompat.GetBuiltInCategory(type?.Category);
            if (builtIn == BuiltInCategory.INVALID) return null;
            return (int)builtIn;
        }
        catch
        {
            return null;
        }
    }

    private static ElementType? FindPrototypeType(Document doc, int? categoryOrdinal)
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
            return collector.Cast<ElementType>().FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private (int Written, int Skipped) WriteParameters(
        Document sourceDoc,
        Document doc,
        ElementType target,
        SystemTypeSnapshot template,
        Dictionary<string, ElementId?> elementIdCache)
    {
        var written = 0;
        var skipped = 0;
        var failCounter = new HotLoopCounter(sampleEvery: 16);

        foreach (var value in template.Values)
        {
            Parameter? param = null;
            try { param = target.LookupParameter(value.ParameterName); }
            catch { /* duplicate-name definitions — treated as missing */ }

            if (param is null || param.IsReadOnly)
            {
                if (failCounter.ShouldLog())
                {
                    SmartConLogger.Debug(
                        $"Parameter '{value.ParameterName}' skipped: " +
                        (param is null ? "not found on target" : "read-only"));
                }
                skipped++;
                continue;
            }

            if (!value.HasValue)
            {
                if (param.HasValue)
                {
                    try
                    {
                        param.ClearValue();
                        written++;
                    }
                    catch (Exception ex)
                    {
                        // Revit: ClearValue is only allowed on shared
                        // parameters. For plain string parameters an empty
                        // string is the canonical "no value" state.
                        var cleared = false;
                        if (param.StorageType == StorageType.String)
                        {
                            try { cleared = param.Set(string.Empty); }
                            catch { /* counted below */ }
                        }
                        if (cleared) written++;
                        else
                        {
                            SmartConLogger.Debug(
                                $"Parameter '{value.ParameterName}' clear failed: {ex.Message}");
                            skipped++;
                        }
                    }
                }
                continue;
            }

            var ok = false;
            try
            {
                ok = param.StorageType switch
                {
                    StorageType.Double => value.ValueNumber.HasValue && param.Set(value.ValueNumber.Value),
                    StorageType.Integer => value.ValueNumber.HasValue && param.Set((int)value.ValueNumber.Value),
                    StorageType.String => param.Set(value.ValueText ?? string.Empty),
                    StorageType.ElementId => TrySetElementId(sourceDoc, doc, param, value, elementIdCache),
                    _ => false,
                };
            }
            catch (Exception ex)
            {
                if (failCounter.ShouldLog())
                {
                    SmartConLogger.Debug(
                        $"Parameter '{value.ParameterName}' write failed: {ex.Message}");
                }
            }

            if (ok) written++;
            else skipped++;
        }

        return (written, skipped);
    }

    private bool TrySetElementId(
        Document sourceDoc,
        Document doc,
        Parameter param,
        SystemParameterValue value,
        Dictionary<string, ElementId?> cache)
    {
        var name = value.ResolvedElementName;
        if (string.IsNullOrEmpty(name)) return false;

        if (!cache.TryGetValue(name!, out var resolved))
        {
            resolved = ResolveElementByName(doc, name!);
            if (resolved is null)
            {
                // Last resort: a material with this name exists in the
                // reference but not in the project — create/update it.
                resolved = _materialSync.SyncMaterial(sourceDoc, doc, name!);
            }
            cache[name!] = resolved;
        }
        if (resolved is null) return false;

        try
        {
            return param.Set(resolved);
        }
        catch
        {
            return false;
        }
    }

    private static ElementId? ResolveElementByName(Document doc, string name)
    {
        using var materials = new FilteredElementCollector(doc).OfClass(typeof(Material));
        var material = materials.Cast<Material>()
            .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        if (material is not null) return material.Id;

        using var types = new FilteredElementCollector(doc).OfClass(typeof(ElementType));
        var type = types.Cast<ElementType>()
            .FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        return type?.Id;
    }
}
