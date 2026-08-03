using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
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
    private readonly ICompoundStructureSyncService _structureSync;

    public SystemTypeSyncService(
        ITransactionService tx,
        IFamilySnapshotExtractor snapshotExtractor,
        ISystemTypeFinder typeFinder,
        IClock clock,
        IMaterialSyncService materialSync,
        ISegmentSyncService segmentSync,
        IFittingDependencyResolver fittingResolver,
        ICompoundStructureSyncService structureSync)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(tx);
        ArgumentNullException.ThrowIfNull(snapshotExtractor);
        ArgumentNullException.ThrowIfNull(typeFinder);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(materialSync);
        ArgumentNullException.ThrowIfNull(segmentSync);
        ArgumentNullException.ThrowIfNull(fittingResolver);
        ArgumentNullException.ThrowIfNull(structureSync);
#else
        if (tx is null) throw new ArgumentNullException(nameof(tx));
        if (snapshotExtractor is null) throw new ArgumentNullException(nameof(snapshotExtractor));
        if (typeFinder is null) throw new ArgumentNullException(nameof(typeFinder));
        if (clock is null) throw new ArgumentNullException(nameof(clock));
        if (materialSync is null) throw new ArgumentNullException(nameof(materialSync));
        if (segmentSync is null) throw new ArgumentNullException(nameof(segmentSync));
        if (fittingResolver is null) throw new ArgumentNullException(nameof(fittingResolver));
        if (structureSync is null) throw new ArgumentNullException(nameof(structureSync));
#endif
        _tx = tx;
        _snapshotExtractor = snapshotExtractor;
        _typeFinder = typeFinder;
        _clock = clock;
        _materialSync = materialSync;
        _segmentSync = segmentSync;
        _fittingResolver = fittingResolver;
        _structureSync = structureSync;
    }

    public SystemTypeSyncResult SyncTypeFromSource(
        Document sourceDoc,
        Document activeDoc,
        string typeName,
        string catalogItemId,
        string versionLabel,
        int sourceRevitVersion,
        string? familyName = null,
        string? familyKey = null)
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

        var sourceTypeId = _typeFinder.FindTypeByName(sourceDoc, typeName, null, familyName, familyKey);
        if (sourceTypeId is null)
        {
            SmartConLogger.Warn(
                $"Type '{typeName}' (family '{familyName ?? "<any>"}', key '{familyKey ?? "<any>"}') not found in the source mini-project. " +
                "[Action: reimport the mini-project into the catalog — its type list is out of sync]");
            return new SystemTypeSyncResult(
                typeName, SystemTypeSyncStatus.NotFoundInSource, 0, 0,
                "Type not found in the source mini-project");
        }

        var sourceType = sourceDoc.GetElement(sourceTypeId) as ElementType;
        var categoryOrdinal = GetCategoryOrdinal(sourceType);
        // #183: the system family of the reference type is the identity key —
        // the target is matched by (family, name, category) and a created
        // type is duplicated from a prototype of the SAME family, never from
        // a foreign one ("Conduit without Fittings" must not update/create
        // types of "Conduit with Fittings"). The caller-supplied familyName
        // locates the exact reference type in the mini-project; the document
        // value is the ground truth for the target match.
        // #190 (ADR-064): the document-computed family key is locale-invariant
        // ground truth — the caller-supplied key is only the locator fallback.
        var effectiveFamilyName = sourceType?.FamilyName ?? familyName;
        var effectiveFamilyKey = sourceType is not null
            ? SystemFamilyKeyResolver.Resolve(sourceType)
            : familyKey;
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
            var targetId = _typeFinder.FindTypeByName(doc, typeName, categoryOrdinal, effectiveFamilyName, effectiveFamilyKey);
            var target = targetId is not null ? doc.GetElement(targetId) as ElementType : null;

            var status = SystemTypeSyncStatus.Updated;
            if (target is null)
            {
                var prototype = FindPrototypeType(doc, categoryOrdinal, effectiveFamilyName, effectiveFamilyKey);
                if (prototype is null)
                {
                    // #183: no same-family prototype — the system family does
                    // not exist in the project and cannot be created via the
                    // API. Skipping is the only safe outcome: duplicating a
                    // FOREIGN family type would corrupt the identity.
                    SmartConLogger.Warn(
                        $"Type '{typeName}' (family '{effectiveFamilyName ?? "<unknown>"}'): the system family does not exist " +
                        "in the project; the type cannot be created. " +
                        "[Action: load any type of this system family into the project manually (e.g. place one element), then retry]");
                    result = new SystemTypeSyncResult(
                        typeName, SystemTypeSyncStatus.FamilyNotFound, 0, 0,
                        $"System family '{effectiveFamilyName ?? "<unknown>"}' not present in the project");
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

            if (template.Structure is not null && target is HostObjAttributes)
            {
                notConverged += _structureSync.SyncStructure(sourceDoc, doc, target, template.Structure);
            }

            // #184 (ADR-065): stairs subtype references (run/landing/supports/
            // cut mark) — synced by name in the same transaction; the
            // subtype's own parameters are written too (live read from the
            // mini-project, ADR-061).
            if (sourceType is StairsType sourceStairs && target is StairsType targetStairs)
            {
                notConverged += SyncStairsSubtypes(sourceDoc, doc, sourceStairs, targetStairs, elementIdCache);
            }

            // ADR-065: railing structure (top rail, handrails, rail list,
            // baluster placement) — live read from the mini-project.
            if (sourceType is RailingType sourceRailing && target is RailingType targetRailing)
            {
                notConverged += SyncRailingStructure(sourceDoc, doc, sourceRailing, targetRailing, elementIdCache);
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
            return collector.Cast<ElementType>().FirstOrDefault(t =>
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

    private (int Written, int Skipped) WriteParameters(
        Document sourceDoc,
        Document doc,
        ElementType target,
        SystemTypeSnapshot template,
        Dictionary<string, ElementId?> elementIdCache)
    {
        var written = 0;
        var skipped = 0;
        // Every skip is logged with its reason: ~30 params per type is far
        // from hot-loop volume, and an unexplained skip list was a blind
        // spot when diagnosing "the parameter did not sync" reports.
        var skippedDetails = new List<string>();

        foreach (var value in template.Values)
        {
            Parameter? param = null;
            try { param = target.LookupParameter(value.ParameterName); }
            catch { /* duplicate-name definitions — treated as missing */ }

            if (param is null || param.IsReadOnly)
            {
                skipped++;
                skippedDetails.Add(param is null
                    ? $"{value.ParameterName}(missing)"
                    : $"{value.ParameterName}(read-only)");
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
                            skippedDetails.Add($"{value.ParameterName}(clear-failed)");
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
                SmartConLogger.Debug(
                    $"Parameter '{value.ParameterName}' write failed: {ex.Message}");
                skippedDetails.Add($"{value.ParameterName}(set-failed)");
            }

            if (ok) written++;
            else
            {
                skipped++;
                // Set returned false without throwing — not covered by the
                // catch above, record the reason explicitly.
                if (!skippedDetails.Any(d => d.StartsWith(value.ParameterName + "(", StringComparison.Ordinal)))
                {
                    skippedDetails.Add($"{value.ParameterName}(set-rejected)");
                }
            }
        }

        if (skippedDetails.Count > 0)
        {
            SmartConLogger.Debug(
                $"Skipped parameters for '{target.Name}': [{string.Join(", ", skippedDetails)}]");
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

    // ── #184 / ADR-065: stairs subtypes + railing structure ─────────────

    /// <summary>
    /// #184 (ADR-065, вариант Б): sync the subtype references of a stairs
    /// type — run/landing/side supports/middle support/cut mark. Each
    /// referenced subtype is found in the target by (class, name) or
    /// created by duplicating a same-class prototype; its own parameters
    /// are written (live read from the mini-project); then the reference
    /// is assigned on the target stairs type. Missing prototype family in
    /// the project → Warn + NotConverged (never a cross-family write).
    /// </summary>
    private int SyncStairsSubtypes(
        Document sourceDoc,
        Document doc,
        StairsType source,
        StairsType target,
        Dictionary<string, ElementId?> elementIdCache)
    {
        var notConverged = 0;
        notConverged += SyncReferencedSubtype(sourceDoc, doc, source.RunType,
            typeof(StairsRunType), null, "RunType", id => target.RunType = id, elementIdCache);
        notConverged += SyncReferencedSubtype(sourceDoc, doc, source.LandingType,
            typeof(StairsLandingType), null, "LandingType", id => target.LandingType = id, elementIdCache);
        // Support types have no dedicated API class — they are plain
        // ElementTypes of OST_StairsSupports (revitapidocs 2026 namespace
        // listing), so they are collected by category.
        notConverged += SyncReferencedSubtype(sourceDoc, doc, source.LeftSideSupportType,
            null, BuiltInCategory.OST_StairsSupports, "LeftSideSupportType", id => target.LeftSideSupportType = id, elementIdCache);
        notConverged += SyncReferencedSubtype(sourceDoc, doc, source.RightSideSupportType,
            null, BuiltInCategory.OST_StairsSupports, "RightSideSupportType", id => target.RightSideSupportType = id, elementIdCache);
        // The setters throw when the target type has no middle supports /
        // the support style is none (revitapidocs InvalidOperationException)
        // — guard on the CURRENT state (parameters were already written).
        if (target.HasMiddleSupports)
        {
            notConverged += SyncReferencedSubtype(sourceDoc, doc, source.MiddleSupportType,
                null, BuiltInCategory.OST_StairsSupports, "MiddleSupportType", id => target.MiddleSupportType = id, elementIdCache);
        }

        var cutMarkParam = target.get_Parameter(BuiltInParameter.STAIRSTYPE_CUTMARK_TYPE);
        if (cutMarkParam is not null && !cutMarkParam.IsReadOnly)
        {
            var sourceCutMarkId = source.get_Parameter(BuiltInParameter.STAIRSTYPE_CUTMARK_TYPE)?.AsElementId();
            notConverged += SyncReferencedSubtype(sourceDoc, doc, sourceCutMarkId,
                typeof(CutMarkType), null, "CutMarkType", id => cutMarkParam.Set(id), elementIdCache);
        }
        return notConverged;
    }

    /// <summary>
    /// Find-or-create a referenced subtype in the target by name (within
    /// its class or category), write its parameters (live read from the
    /// mini-project) and assign the reference. Missing prototype in the
    /// project → Warn + NotConverged (never a cross-family write).
    /// </summary>
    private int SyncReferencedSubtype(
        Document sourceDoc,
        Document doc,
        ElementId? sourceSubtypeId,
        Type? subtypeClass,
        BuiltInCategory? subtypeCategory,
        string slotName,
        Action<ElementId> assign,
        Dictionary<string, ElementId?> elementIdCache)
    {
        if (sourceSubtypeId is null || sourceSubtypeId == ElementId.InvalidElementId)
            return 0;

        var sourceSubtype = sourceDoc.GetElement(sourceSubtypeId) as ElementType;
        if (sourceSubtype is null)
        {
            SmartConLogger.Warn(
                $"Subtype ({slotName}) unreadable in the source mini-project. " +
                "[Action: reimport the category into the catalog]");
            return 1;
        }

        var targetSubtype = CollectSubtypeCandidates(doc, subtypeClass, subtypeCategory)
            .FirstOrDefault(t => string.Equals(t.Name, sourceSubtype.Name, StringComparison.OrdinalIgnoreCase));
        if (targetSubtype is null)
        {
            var prototype = CollectSubtypeCandidates(doc, subtypeClass, subtypeCategory)
                .FirstOrDefault();
            if (prototype is null)
            {
                var scope = subtypeClass?.Name ?? subtypeCategory?.ToString() ?? "?";
                SmartConLogger.Warn(
                    $"Subtype '{sourceSubtype.Name}' ({slotName}): no {scope} prototype " +
                    "in the project — the subtype family cannot be created via the API. " +
                    "[Action: place any element using this subtype family in the project, then retry]");
                return 1;
            }
            targetSubtype = prototype.Duplicate(sourceSubtype.Name);
        }

        var subtypeTemplate = _snapshotExtractor.ExtractSingleSystemType(sourceDoc, sourceSubtypeId);
        if (subtypeTemplate is not null)
        {
            WriteParameters(sourceDoc, doc, targetSubtype, subtypeTemplate, elementIdCache);
        }

        try
        {
            assign(targetSubtype.Id);
            return 0;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Stairs subtype assignment failed ({slotName} = '{sourceSubtype.Name}'): {ex.Message} " +
                "[Action: check the log; the subtype data is synced, only the reference assignment failed]");
            return 1;
        }
    }

    /// <summary>
    /// ADR-065: sync the railing structure — top rail / handrails (type by
    /// name + scalars), the non-continuous rail list (cleared and rebuilt
    /// from the reference; profile by family-qualified name, material by
    /// name with the material-sync fallback) and the baluster placement
    /// scalars. Every rejected member counts as NotConverged — no silent
    /// skips.
    /// </summary>
    private int SyncRailingStructure(
        Document sourceDoc,
        Document doc,
        RailingType source,
        RailingType target,
        Dictionary<string, ElementId?> elementIdCache)
    {
        var notConverged = 0;

        notConverged += TrySetRailingMember("TopRailHeight", () => target.TopRailHeight = source.TopRailHeight);
        // The handrail height/offset properties on RailingType are READ-ONLY
        // (revitapidocs) — they follow the assigned handrail TYPE. So the
        // handrail/top-rail types are synced as referenced subtypes (their
        // own parameters included) and only the reference + position are
        // assigned on the railing type.
        notConverged += SyncReferencedSubtype(sourceDoc, doc, source.TopRailType,
            typeof(TopRailType), null, "TopRailType", id => target.TopRailType = id, elementIdCache);
        notConverged += TrySetRailingMember("PrimaryHandRailPosition", () => target.PrimaryHandRailPosition = source.PrimaryHandRailPosition);
        notConverged += SyncReferencedSubtype(sourceDoc, doc, source.PrimaryHandrailType,
            typeof(HandRailType), null, "PrimaryHandrailType", id => target.PrimaryHandrailType = id, elementIdCache);
        notConverged += TrySetRailingMember("SecondaryHandRailPosition", () => target.SecondaryHandRailPosition = source.SecondaryHandRailPosition);
        notConverged += SyncReferencedSubtype(sourceDoc, doc, source.SecondaryHandrailType,
            typeof(HandRailType), null, "SecondaryHandrailType", id => target.SecondaryHandrailType = id, elementIdCache);

        using var sourceStructure = source.RailStructure;
        using var targetStructure = target.RailStructure;
        if (sourceStructure is not null && targetStructure is not null)
        {
            while (targetStructure.GetNonContinuousRailCount() > 0)
            {
                targetStructure.RemoveNonContinuousRail(0);
            }

            var railCount = sourceStructure.GetNonContinuousRailCount();
            for (var i = 0; i < railCount; i++)
            {
                using var sourceRail = sourceStructure.GetNonContinuousRail(i);
                if (sourceRail is null) continue;

                // AddNonContinuousRail throws ArgumentException (invalid/
                // duplicate name, height above the railing height — e.g.
                // when TopRailHeight was rejected above) — per-rail
                // granularity: one bad rail must not fail the whole type.
                NonContinuousRailInfo? newRail;
                try
                {
                    targetStructure.AddNonContinuousRail(sourceRail.Name, sourceRail.Height, sourceRail.Offset);
                    newRail = targetStructure.GetNonContinuousRail(
                        targetStructure.GetNonContinuousRailCount() - 1);
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn(
                        $"Railing rail '{sourceRail.Name}' rejected: {ex.Message} " +
                        "[Action: check the rail name/height against the railing type height]");
                    notConverged++;
                    continue;
                }

                using (newRail)
                {
                    if (newRail is null)
                    {
                        notConverged++;
                        continue;
                    }

                    var profileName = ResolveQualifiedSymbolName(sourceDoc, sourceRail.ProfileId);
                    if (profileName is not null)
                    {
                        var profileId = ResolveFamilySymbolByQualifiedName(doc, profileName);
                        if (profileId is not null)
                        {
                            notConverged += TrySetRailingMember($"Rail[{sourceRail.Name}].Profile",
                                () => newRail.ProfileId = profileId);
                        }
                        else
                        {
                            SmartConLogger.Warn(
                                $"Railing rail '{sourceRail.Name}': profile '{profileName}' not found in the project. " +
                                "[Action: load the profile family into the project, then retry the sync]");
                            notConverged++;
                        }
                    }

                    var materialName = ResolveElementName(sourceDoc, sourceRail.MaterialId);
                    if (materialName is not null)
                    {
                        var materialId = ResolveElementByName(doc, materialName)
                            ?? _materialSync.SyncMaterial(sourceDoc, doc, materialName);
                        if (materialId is not null)
                        {
                            notConverged += TrySetRailingMember($"Rail[{sourceRail.Name}].Material",
                                () => newRail.MaterialId = materialId);
                        }
                        else
                        {
                            notConverged++;
                        }
                    }
                }
            }
        }

        using var sourcePlacement = source.BalusterPlacement;
        using var targetPlacement = target.BalusterPlacement;
        if (sourcePlacement is not null && targetPlacement is not null)
        {
            notConverged += TrySetRailingMember("UseBalusterPerTreadOnStairs",
                () => targetPlacement.UseBalusterPerTreadOnStairs = sourcePlacement.UseBalusterPerTreadOnStairs);
            notConverged += TrySetRailingMember("BalusterPerTreadNumber",
                () => targetPlacement.BalusterPerTreadNumber = sourcePlacement.BalusterPerTreadNumber);
            notConverged += TrySetRailingMember("BalusterPerTreadFamily", () =>
            {
                var qualified = ResolveQualifiedSymbolName(sourceDoc, sourcePlacement.BalusterPerTreadFamilyId);
                if (qualified is null) return;
                var id = ResolveFamilySymbolByQualifiedName(doc, qualified);
                if (id is null)
                {
                    // No silent skips (ADR-065 §5): a missing baluster
                    // family is a visible non-convergence.
                    throw new InvalidOperationException(
                        $"baluster per-tread family '{qualified}' not found in the project");
                }
                targetPlacement.BalusterPerTreadFamilyId = id;
            });

            using var sourcePattern = sourcePlacement.BalusterPattern;
            using var targetPattern = targetPlacement.BalusterPattern;
            if (sourcePattern is not null && targetPattern is not null)
            {
                // BalusterPattern.Length is read-only (computed from the
                // baluster list) — the scalars below carry the content.
                notConverged += TrySetRailingMember("BalusterPattern.DistributionJustification",
                    () => targetPattern.DistributionJustification = sourcePattern.DistributionJustification);
                notConverged += TrySetRailingMember("BalusterPattern.BreakPattern",
                    () => targetPattern.BreakPattern = sourcePattern.BreakPattern);
                notConverged += TrySetRailingMember("BalusterPattern.EndSpace",
                    () => targetPattern.EndSpace = sourcePattern.EndSpace);
                notConverged += TrySetRailingMember("BalusterPattern.ExcessLengthFillSpacing",
                    () => targetPattern.ExcessLengthFillSpacing = sourcePattern.ExcessLengthFillSpacing);
            }
        }

        return notConverged;
    }

    private static List<ElementType> CollectSubtypeCandidates(
        Document doc, Type? subtypeClass, BuiltInCategory? subtypeCategory)
    {
        IEnumerable<ElementType> candidates = new FilteredElementCollector(doc)
            .WhereElementIsElementType()
            .Cast<ElementType>();
        if (subtypeClass is not null)
        {
            candidates = candidates.Where(t => subtypeClass.IsInstanceOfType(t));
        }
        if (subtypeCategory is not null)
        {
            var categoryId = Core.Compatibility.ElementIdCompat.Create((int)subtypeCategory.Value);
            candidates = candidates.Where(t => t.Category is not null && t.Category.Id == categoryId);
        }
        return candidates.ToList();
    }

    private static int TrySetRailingMember(string memberName, Action write)
    {
        try
        {
            write();
            return 0;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Railing structure member '{memberName}' rejected: {ex.Message} " +
                "[Action: see the log; the remaining members were applied]");
            return 1;
        }
    }

    private static string? ResolveElementName(Document doc, ElementId? id)
    {
        if (id is null || id == ElementId.InvalidElementId) return null;
        try { return doc.GetElement(id)?.Name; }
        catch { return null; }
    }

    private static string? ResolveQualifiedSymbolName(Document doc, ElementId? id)
    {
        if (id is null || id == ElementId.InvalidElementId) return null;
        try
        {
            return doc.GetElement(id) switch
            {
                FamilySymbol symbol => $"{symbol.Family?.Name}:{symbol.Name}",
                var element => element?.Name,
            };
        }
        catch
        {
            return null;
        }
    }

    private static ElementId? ResolveFamilySymbolByQualifiedName(Document doc, string qualifiedName)
    {
        var separator = qualifiedName.IndexOf(':');
        if (separator <= 0) return null;
        var familyName = qualifiedName[..separator];
        var typeName = qualifiedName[(separator + 1)..];
        return new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .FirstOrDefault(s =>
                string.Equals(s.Family?.Name, familyName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(s.Name, typeName, StringComparison.OrdinalIgnoreCase))
            ?.Id;
    }
}
