using System.Collections;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using Electrical = Autodesk.Revit.DB.Electrical;

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
    private readonly IFamilyRoutingRuleRepository? _routingRuleRepository;
    private readonly ISegmentRuleRepository? _segmentRuleRepository;

    public SystemTypeSyncService(
        ITransactionService tx,
        IFamilySnapshotExtractor snapshotExtractor,
        ISystemTypeFinder typeFinder,
        IClock clock,
        IMaterialSyncService materialSync,
        ISegmentSyncService segmentSync,
        IFittingDependencyResolver fittingResolver,
        ICompoundStructureSyncService structureSync,
        IFamilyRoutingRuleRepository? routingRuleRepository = null,
        ISegmentRuleRepository? segmentRuleRepository = null)
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
        _routingRuleRepository = routingRuleRepository;
        _segmentRuleRepository = segmentRuleRepository;
    }

    public SystemTypeSyncResult SyncTypeFromSource(
        Document sourceDoc,
        Document activeDoc,
        string typeName,
        string catalogItemId,
        string versionLabel,
        int sourceRevitVersion,
        string? familyName = null,
        string? familyKey = null,
        int? categoryOrdinal = null)
        => SyncTypeFromSourceCore(
            sourceDoc, activeDoc, typeName, catalogItemId, versionLabel,
            sourceRevitVersion, familyName, familyKey, categoryOrdinal,
            stagingMode: false);

    public SystemTypeSyncResult StageTypeFromSource(
        Document sourceDoc,
        Document stagingDoc,
        string typeName,
        int? categoryOrdinal = null,
        string? familyName = null,
        string? familyKey = null)
        => SyncTypeFromSourceCore(
            sourceDoc, stagingDoc, typeName, string.Empty, string.Empty,
            0, familyName, familyKey, categoryOrdinal,
            stagingMode: true);

    private SystemTypeSyncResult SyncTypeFromSourceCore(
        Document sourceDoc,
        Document activeDoc,
        string typeName,
        string catalogItemId,
        string versionLabel,
        int sourceRevitVersion,
        string? familyName,
        string? familyKey,
        int? categoryOrdinal,
        bool stagingMode)
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

        // Manual test 2026-08-04 (round 3): the reference lookup MUST be
        // category-scoped whenever the caller knows the category. Without it
        // the name match can bind an ElementType of a completely different
        // category that shares the name and the degenerate family key
        // ('Single') — e.g. a DistributionSysType/VoltageType named
        // "По умолчанию" was synced instead of the real WireType, and its
        // parameters + ES marker were written to the wrong element kind.
        var sourceTypeId = _typeFinder.FindTypeByName(sourceDoc, typeName, categoryOrdinal, familyName, familyKey);
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
        // The caller-supplied category is authoritative (catalog truth);
        // the document-derived value is the fallback when the catalog row
        // carries no category (legacy data).
        var effectiveCategoryOrdinal = categoryOrdinal ?? GetCategoryOrdinal(sourceType);
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
        if (stagingMode)
        {
            // ADR-072: the staging project carries no fittings — the routing
            // written into the mini is SLIM (segment rules + no-part rules
            // only), so no fitting family (and no family-internal material
            // record) ever enters the mini-project.
            template = template with { Routing = SlimRoutingForStaging(template.Routing) };
        }
        else
        {
            // ADR-072 (plan item 3): the slim mini-project carries no fittings —
            // routing syncs from the catalog DB (V34), not from the mini. The
            // legacy fallback (pre-V34 versions without stored routing) keeps
            // reading the mini — an empty DB snapshot would otherwise erase the
            // target's fitting rules.
            var miniRouting = template.Routing;
            template = SubstituteRoutingFromDb(template, catalogItemId);
            // Audit M11 (second line of defense): the catalog held no stored
            // routing rows at all, so the SLIM mini routing would be applied
            // verbatim — erasing the project type's fitting rules as
            // "target-only". An ambiguous "catalog knows nothing" state must
            // never converge projects: leave the live routing untouched.
            // (A legitimately routing-less type has marker settings rows in
            // the DB — the substitution then returns a fresh empty routing
            // and this guard does not engage. A NULL repository means "no
            // catalog in play" — tests/legacy wiring where the source
            // document IS the truth — the guard must not engage either.)
            if (miniRouting is not null
                && _routingRuleRepository is not null
                && ReferenceEquals(template.Routing, miniRouting)
                && HasNoFittingParts(miniRouting))
            {
                SmartConLogger.Warn(
                    $"Type '{typeName}': the catalog holds no stored routing rows and the mini-project is slim — " +
                    "the live routing is left untouched (catalog has no opinion). " +
                    "[Action: импортируйте эталон из живого проекта или настройте трассировку во вкладке «Трассировка», затем повторите sync]");
                template = template with { Routing = null };
            }
        }

        // Phase A — fitting dependencies. LoadFamily throws when the target
        // document is modifiable, so catalog loads happen BEFORE the sync
        // transaction opens. Consequence: when the sync transaction later
        // rolls back, an already-loaded fitting stays in the project without
        // a routing rule pointing to it. This is acceptable — LoadFamily
        // cannot be rolled back by design, the fitting is a regular catalog
        // family with its own stale lifecycle, and the next sync reuses it.
        // Staging skips Phase A entirely: the mini-project must stay slim
        // (no fittings), and its routing holds no fitting references.
        if (!stagingMode && template.Routing is not null)
        {
            EnsureFittingDependencies(activeDoc, template.Routing, sourceRevitVersion, catalogItemId);
        }

        SystemTypeSyncResult? result = null;
        var committed = _tx.RunInTransaction(activeDoc, $"SmartCon: Sync system type '{typeName}'", doc =>
        {
            var targetId = _typeFinder.FindTypeByName(doc, typeName, effectiveCategoryOrdinal, effectiveFamilyName, effectiveFamilyKey);
            var target = targetId is not null ? doc.GetElement(targetId) as ElementType : null;

            var status = SystemTypeSyncStatus.Updated;
            if (target is null)
            {
                var prototype = FindPrototypeType(doc, effectiveCategoryOrdinal, effectiveFamilyName, effectiveFamilyKey);
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
            var portedGuids = EnsureMissingSharedParameters(sourceDoc, doc, sourceType, target, template);
            var (written, skipped) = WriteParameters(sourceDoc, doc, target, template, elementIdCache, portedGuids);

            var notConverged = 0;
            if (template.Routing is not null && target is MEPCurveType mepCurveType)
            {
                // ADR-072 (plan item 2b): manager-less types (flex/conduit/
                // cable-tray — RoutingPreferenceManager is null, probe-
                // verified) sync their routing as plain parameter values;
                // pipe/duct go through the RoutingPreferenceManager.
                bool hasRoutingManager;
                using (var probe = mepCurveType.RoutingPreferenceManager)
                {
                    hasRoutingManager = probe is not null;
                }
                notConverged = hasRoutingManager
                    ? SyncRoutingPreferences(sourceDoc, doc, mepCurveType, template.Routing)
                    : SyncRoutingParamsFromDb(doc, mepCurveType, template.Routing);
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

            // FHV5: wire settings graph (material/temperature rating/
            // insulation/max size/conduit + neutral scalars) — WireType
            // properties backed by ElectricalSetting, invisible to the
            // generic parameter pipeline (manual test 2026-08-04: a wire
            // material change did not sync).
            if (sourceType is Electrical.WireType sourceWire && target is Electrical.WireType targetWire)
            {
                notConverged += SyncWireSettings(doc, sourceWire, targetWire);
            }

            if (!stagingMode)
            {
                RevitFamilyVersionStore.WriteEntityToElement(target, new FamilyVersion(
                    SchemaVersion: FamilyVersion.CurrentSchemaVersion,
                    CatalogItemId: catalogItemId,
                    VersionLabel: versionLabel,
                    LoadedAtUtc: _clock.UtcNow,
                    SourceRevitVersion: sourceRevitVersion));
            }

            result = new SystemTypeSyncResult(
                typeName, status, written, skipped, NotConvergedCount: notConverged,
                // Owner decision 2026-08-31 (audit M6): the parameter port
                // into a LIVE project must be visible to the user, never a
                // silent side effect (staging ports are internal mechanics).
                PortedParameterNames: !stagingMode && portedGuids.Count > 0
                    ? portedGuids.Keys.ToList()
                    : null);
        });

        if (!committed || result is null)
        {
            // Stress test 2026-08-05: when the transaction rolled back, the
            // `result` built INSIDE it is a lie (status Created/Updated for
            // a type that does not exist) — it used to leak into the caller
            // as "1/1 types synchronized" right after the rollback warning.
            SmartConLogger.Warn(
                $"Type '{typeName}': sync transaction failed. " +
                "[Action: type skipped, batch continues; check the log for the transaction error]");
            return new SystemTypeSyncResult(
                typeName, SystemTypeSyncStatus.Failed, 0, 0,
                committed ? "Transaction produced no result" : "Transaction rolled back");
        }

        SmartConLogger.Info(
            $"Type '{typeName}': {result.Status}, {result.ParametersWritten} parameters written, " +
            $"{result.ParametersSkipped} skipped, not converged: {result.NotConvergedCount}.");
        return result;
    }

    /// <summary>
    /// ADR-072 staging: reduces the source routing to what a slim
    /// mini-project may physically hold — segment rules (resolved by the
    /// segment synchronizer into a created segment) and no-part rules
    /// ("Нет" — legal content needing no fitting). Every fitting reference
    /// is dropped, so no fitting family and no family-internal material
    /// record enters the staging project (the #254 duplication class is
    /// absent by construction).
    /// </summary>
    private static RoutingPreferencesSnapshot? SlimRoutingForStaging(RoutingPreferencesSnapshot? routing)
    {
        if (routing is null)
            return null;
        var slimRules = routing.Rules
            .Where(r => r.GroupType == (int)RoutingPreferenceRuleGroupType.Segments
                || RoutingGroupKeys.IsParamGroup(r.GroupKey)
                || r.PartName is null)
            .Select(r => RoutingGroupKeys.IsParamGroup(r.GroupKey) && r.PartName is not null
                // Parameter groups (flex/conduit/tray) are kept but forced
                // to "Нет": a custom template prototype could carry fitting
                // references in its routing parameters, and Duplicate()
                // would inherit them into the slim mini.
                ? r with { PartName = null }
                : r)
            .ToList();
        return slimRules.Count == routing.Rules.Count
            ? routing
            : routing with { Rules = slimRules };
    }

    /// <summary>
    /// Slim-mini signature (audit M11): the routing carries no fitting part
    /// at all — every rule is either a no-part rule or belongs to the
    /// Segments group (a segment is not a fitting). A full legacy mini
    /// always has fitting parts; a legitimately routing-less type is
    /// excluded by the caller (its marker settings rows make the DB
    /// substitution return a fresh empty routing instead of the mini one).
    /// </summary>
    private static bool HasNoFittingParts(RoutingPreferencesSnapshot routing)
        => routing.Rules.All(r =>
            r.PartName is null || r.GroupType == (int)RoutingManagerGroup.Segments);

    /// <summary>
    /// ADR-072 World B: replaces the routing of the extracted template with
    /// the catalog's item-level routing links (V37 — routing is a catalog-
    /// family link, not version content). Legacy fallbacks (all non-
    /// destructive — never erase target rules on missing data): no
    /// repository (tests/legacy wiring), no item-level rows yet (pre-World-B
    /// version — the current version's V34 rows), no rows for THIS type
    /// (routing-less or added later), DB read failure — the mini-project
    /// routing is kept.
    /// </summary>
    private SystemTypeSnapshot SubstituteRoutingFromDb(SystemTypeSnapshot template, string catalogItemId)
    {
        if (_routingRuleRepository is null)
            return template;

        try
        {
            return Core.Threading.AsyncBridge.RunSync(async () =>
            {
                var (rules, settings) = await _routingRuleRepository
                    .HasAnyForItemAsync(catalogItemId).ConfigureAwait(false)
                    ? await _routingRuleRepository.ReadForItemAsync(catalogItemId).ConfigureAwait(false)
                    : !await _routingRuleRepository.HasRulesForCurrentVersionAsync(catalogItemId)
                            .ConfigureAwait(false)
                        ? default
                        : await _routingRuleRepository.ReadForCurrentVersionAsync(catalogItemId).ConfigureAwait(false);
                if (rules is null)
                    return template;

                // FHV21: segment rules compose from the per-version store of
                // the CURRENT version (fittings stay item-level). Null repo
                // (tests, legacy wiring) = stored Segments rows as before.
                if (_segmentRuleRepository is not null)
                {
                    var perVersionSegments = await _segmentRuleRepository
                        .ReadForCurrentVersionAsync(catalogItemId).ConfigureAwait(false);
                    rules = SegmentRuleComposition.Compose(rules, perVersionSegments);
                }

                var dbRouting = RoutingRuleRecordMapper.ToSnapshot(
                    template.Name, template.FamilyKey ?? string.Empty, rules, settings!);
                return dbRouting is null ? template : template with { Routing = dbRouting };
            });
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Routing read from the catalog DB failed for '{template.Name}': {ex.Message} " +
                "[Action: routing is read from the mini-project (legacy mode); check the catalog DB and re-run the sync]");
            return template;
        }
    }

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

    private (int Written, int Skipped) WriteParameters(
        Document sourceDoc,
        Document doc,
        ElementType target,
        SystemTypeSnapshot template,
        Dictionary<string, ElementId?> elementIdCache,
        IReadOnlyDictionary<string, Guid>? portedGuids = null)
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
            // Just-ported shared parameters are addressed by GUID —
            // LookupParameter is ambiguous when same-name definitions exist
            // and was observed to miss freshly bound parameters entirely.
            if (portedGuids is not null
                && portedGuids.TryGetValue(value.ParameterName, out var portedGuid))
            {
                try { param = target.get_Parameter(portedGuid); } catch { /* fall through */ }
            }
            if (param is null)
            {
                try { param = target.LookupParameter(value.ParameterName); }
                catch { /* duplicate-name definitions — treated as missing */ }
            }

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

    /// <summary>
    /// The clean/staging project template carries no shared project
    /// parameters of the source (ADSK_*/BP_* etc.) — without porting their
    /// definitions the staged type loses them, and any reimport from the
    /// mini-project then sees a phantom VALUES diff against the version
    /// imported from the live project. Port every missing shared definition
    /// (same GUID, same data type, type-bound to the target category) so the
    /// staged file is a parameter-complete copy of the source type.
    /// </summary>
    private Dictionary<string, Guid> EnsureMissingSharedParameters(
        Document sourceDoc,
        Document doc,
        ElementType? sourceType,
        ElementType target,
        SystemTypeSnapshot template)
    {
        var portedGuids = new Dictionary<string, Guid>(StringComparer.Ordinal);
        if (sourceType is null) return portedGuids;

        // Presence is checked via Element.Parameters — LookupParameter is
        // unreliable when same-name definitions exist (it may throw or pick
        // a ghost clone).
        var targetNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (Parameter p in target.Parameters)
        {
            var n = p.Definition?.Name;
            if (!string.IsNullOrEmpty(n)) targetNames.Add(n!);
        }

        var missingSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in template.Values)
        {
            if (!targetNames.Contains(value.ParameterName))
                missingSet.Add(value.ParameterName);
        }
        if (missingSet.Count == 0) return portedGuids;

        // Resolve the EXACT definition each missing parameter is bound to in
        // the source (InternalDefinition.Id → ParameterElement). A name
        // search is NOT safe: a project can carry several definitions with
        // the same name (different GUIDs — e.g. a visible parameter plus an
        // invisible clone, BOTH enumerated by Element.Parameters). Prefer
        // the definition that carries a value; porting the empty clone
        // produces a phantom VALUES diff on mini-project reimport.
        // SHARED parameters keep their GUID. Non-shared PROJECT parameters
        // cannot be recreated as such (no public API) — they are ported as
        // shared definitions with a fresh GUID: the canonical VALUES string
        // carries name/storage/value only (no GUID), so the staged file
        // stays hash-identical to the source (owner stress test 2026-08-30:
        // the «Тип трубопровода» project parameter was lost in staging and
        // every reimport produced a phantom version).
        var chosen = new Dictionary<string, Parameter>(StringComparer.Ordinal);
        foreach (Parameter sp in sourceType.Parameters)
        {
            var n = sp.Definition?.Name;
            if (string.IsNullOrEmpty(n) || !missingSet.Contains(n!)) continue;
            if (sp.Definition is not InternalDefinition internalDef
                || internalDef.BuiltInParameter != BuiltInParameter.INVALID)
                continue; // built-ins already exist on the target
            if (!chosen.TryGetValue(n!, out var current) || (!current.HasValue && sp.HasValue))
                chosen[n!] = sp;
        }

        var portPlan = new List<(string Name, Guid Guid, Parameter SourceParam)>();
        foreach (var missingName in missingSet)
        {
            if (!chosen.TryGetValue(missingName, out var sourceParam)) continue;

            Guid portGuid;
            if (sourceParam.IsShared
                && sourceParam.Definition is InternalDefinition sharedDef
                && sourceDoc.GetElement(sharedDef.Id) is SharedParameterElement sharedElement)
            {
                portGuid = sharedElement.GuidValue;
            }
            else if (TryFindPortedDefinition(doc, missingName, null, out var earlierGuid))
            {
                // Audit L23: an earlier type of this batch already ported a
                // NON-shared parameter under the same name — reuse its GUID
                // (and extend its binding below) instead of creating a
                // same-name duplicate definition.
                portGuid = earlierGuid;
            }
            else
            {
                portGuid = Guid.NewGuid();
            }

            portPlan.Add((missingName, portGuid, sourceParam));
        }
        if (portPlan.Count == 0)
        {
            SmartConLogger.Debug(
                $"Staged type '{target.Name}': {missingSet.Count} parameter(s) missing in the target " +
                "but none is a portable custom parameter of the source — nothing to port");
            return portedGuids;
        }

        var app = doc.Application;
        var originalFile = app.SharedParametersFilename;
        var tempFile = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"smartcon-shared-{Guid.NewGuid():N}.txt");
        var created = 0;
        var failed = new List<string>();
        try
        {
            System.IO.File.WriteAllText(tempFile, string.Empty);
            app.SharedParametersFilename = tempFile;
            var definitionFile = app.OpenSharedParameterFile();
            if (definitionFile is null)
            {
                SmartConLogger.Warn(
                    "Shared parameter port: OpenSharedParameterFile returned null for the temp file. " +
                    "[Action: staged types stay parameter-incomplete; reimport will show a VALUES diff]");
                return portedGuids;
            }

            var group = definitionFile.Groups.Create("SmartCon");
            foreach (var (name, portGuid, sourceParam) in portPlan)
            {
                var options = CreatePortedDefinitionOptions(name, sourceParam);
                if (options is null)
                {
                    failed.Add(name);
                    continue;
                }
                options.GUID = portGuid;
                // The source parameter reached the snapshot via
                // Element.Parameters, i.e. it is visible there — the ported
                // definition must stay visible or the extraction on reimport
                // would skip it.
                options.Visible = true;

                var definition = group.Definitions.Create(options);
                if (definition is null)
                {
                    failed.Add(name);
                    continue;
                }

                var categories = app.Create.NewCategorySet();
                categories.Insert(target.Category);
                var binding = app.Create.NewTypeBinding(categories);
                if (doc.ParameterBindings.Insert(definition, binding))
                {
                    created++;
                    portedGuids[name] = portGuid;
                }
                // Audit L23: a binding for this parameter already exists
                // (shared GUID ported for another category of this batch,
                // or the same non-shared name ported earlier) — extend it
                // with this type's category instead of failing.
                else if (TryExtendPortedBinding(doc, name, portGuid, target.Category))
                {
                    created++;
                    portedGuids[name] = portGuid;
                }
                else failed.Add(name);
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Shared parameter port to the staged project failed: {ex.GetType().Name}: {ex.Message} " +
                "[Action: staged types stay parameter-incomplete; reimport will show a VALUES diff]");
        }
        finally
        {
            try { app.SharedParametersFilename = originalFile; } catch { /* app-level state, best effort */ }
            try { System.IO.File.Delete(tempFile); } catch { /* temp residue is harmless */ }
        }

        if (created > 0)
        {
            SmartConLogger.Info(
                $"Staged type '{target.Name}': ported {created} shared parameter definition(s) " +
                $"from the source project: [{string.Join(", ", portedGuids.Keys)}]");
        }
        if (failed.Count > 0)
        {
            SmartConLogger.Debug(
                $"Staged type '{target.Name}': shared parameter port failed for [{string.Join(", ", failed)}]");
        }
        return portedGuids;
    }

    /// <summary>
    /// Finds a shared parameter definition already ported into the document
    /// by name (and optionally by GUID). Used by the batch port (audit L23).
    /// </summary>
    private static bool TryFindPortedDefinition(Document doc, string name, Guid? guid, out Guid foundGuid)
    {
        var iterator = doc.ParameterBindings.ForwardIterator();
        while (iterator.MoveNext())
        {
            if (iterator.Key is not InternalDefinition internalDef
                || !string.Equals(internalDef.Name, name, StringComparison.Ordinal))
                continue;
            if (doc.GetElement(internalDef.Id) is not SharedParameterElement sharedElement)
                continue;
            if (guid is not null && sharedElement.GuidValue != guid.Value)
                continue;
            foundGuid = sharedElement.GuidValue;
            return true;
        }
        foundGuid = Guid.Empty;
        return false;
    }

    /// <summary>
    /// Adds the category to the existing ported binding of the same
    /// name/GUID (audit L23) — <c>ParameterBindings.Insert</c> rejects a
    /// second binding of an already-bound definition.
    /// </summary>
    private static bool TryExtendPortedBinding(Document doc, string name, Guid guid, Category category)
    {
        var map = doc.ParameterBindings;
        var iterator = map.ForwardIterator();
        while (iterator.MoveNext())
        {
            if (iterator.Key is not InternalDefinition internalDef
                || !string.Equals(internalDef.Name, name, StringComparison.Ordinal))
                continue;
            if (doc.GetElement(internalDef.Id) is not SharedParameterElement sharedElement
                || sharedElement.GuidValue != guid)
                continue;
            if (iterator.Current is not ElementBinding existingBinding
                || existingBinding.Categories.Contains(category))
                return true; // already bound for this category — nothing to do
            existingBinding.Categories.Insert(category);
            return map.ReInsert(internalDef, existingBinding);
        }
        return false;
    }

    private static ExternalDefinitionCreationOptions? CreatePortedDefinitionOptions(
        string name, Parameter sourceParam)
    {
#if REVIT2022_OR_GREATER
        var dataType = Compatibility.RevitUnitsCompat.GetDataType(sourceParam.Definition);
        if (dataType is null) return null;
        return new ExternalDefinitionCreationOptions(name, dataType);
#else
        try
        {
#pragma warning disable CS0618
            return new ExternalDefinitionCreationOptions(name, sourceParam.Definition.ParameterType);
#pragma warning restore CS0618
        }
        catch
        {
            return null;
        }
#endif
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
        // Support types have no dedicated API class — their elements live in
        // OST_StairsStringerCarriage (RevitLookup/Autodesk forums: the
        // OST_StairsSupports category object exists but no element carries
        // it — collecting by it finds NOTHING), so they are collected by
        // that category.
        notConverged += SyncReferencedSubtype(sourceDoc, doc, source.LeftSideSupportType,
            null, BuiltInCategory.OST_StairsStringerCarriage, "LeftSideSupportType", id => target.LeftSideSupportType = id, elementIdCache);
        notConverged += SyncReferencedSubtype(sourceDoc, doc, source.RightSideSupportType,
            null, BuiltInCategory.OST_StairsStringerCarriage, "RightSideSupportType", id => target.RightSideSupportType = id, elementIdCache);
        // The setters throw when the target type has no middle supports /
        // the support style is none (revitapidocs InvalidOperationException)
        // — guard on the CURRENT state (parameters were already written).
        if (target.HasMiddleSupports)
        {
            notConverged += SyncReferencedSubtype(sourceDoc, doc, source.MiddleSupportType,
                null, BuiltInCategory.OST_StairsStringerCarriage, "MiddleSupportType", id => target.MiddleSupportType = id, elementIdCache);
        }
        else if (IsValidId(source.MiddleSupportType))
        {
            // ADR-065 §5 — never skip silently: the reference HAS a middle
            // support but the target's "Middle Support" flag stayed off
            // (its parameter write was skipped/failed) — report the residue.
            SmartConLogger.Warn(
                $"Stairs '{target.Name}': MiddleSupportType not synced — target HasMiddleSupports=false " +
                $"while the reference uses '{source.MiddleSupportType}'. " +
                "[Action: enable «Промежуточные опоры» on the target stairs type and re-run «Обновить»]");
            notConverged++;
        }

        var cutMarkParam = target.get_Parameter(BuiltInParameter.STAIRSTYPE_CUTMARK_TYPE);
        if (cutMarkParam is not null && !cutMarkParam.IsReadOnly)
        {
            var sourceCutMarkId = source.get_Parameter(BuiltInParameter.STAIRSTYPE_CUTMARK_TYPE)?.AsElementId();
            notConverged += SyncReferencedSubtype(sourceDoc, doc, sourceCutMarkId,
                typeof(CutMarkType), null, "CutMarkType", id => cutMarkParam.Set(id), elementIdCache);
        }

        // Read-back ground truth (manual test 2026-08-04: supports reported
        // as not syncing despite a clean run) — what the target type
        // actually references AFTER all assignments.
        SmartConLogger.Debug(
            $"Stairs '{target.Name}' subtype read-back: " +
            $"Run='{ResolveElementName(doc, target.RunType)}', Landing='{ResolveElementName(doc, target.LandingType)}', " +
            $"Left='{ResolveElementName(doc, target.LeftSideSupportType)}', " +
            $"Right='{ResolveElementName(doc, target.RightSideSupportType)}', " +
            $"Middle='{(target.HasMiddleSupports ? ResolveElementName(doc, target.MiddleSupportType) : "<none>")}' " +
            $"(source: Run='{ResolveElementName(sourceDoc, source.RunType)}', " +
            $"Landing='{ResolveElementName(sourceDoc, source.LandingType)}', " +
            $"Left='{ResolveElementName(sourceDoc, source.LeftSideSupportType)}', " +
            $"Right='{ResolveElementName(sourceDoc, source.RightSideSupportType)}').");
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
            SmartConLogger.Debug(
                $"Subtype '{sourceSubtype.Name}' ({slotName}): created in the project by duplicating '{prototype.Name}'.");
        }
        else
        {
            SmartConLogger.Debug(
                $"Subtype '{sourceSubtype.Name}' ({slotName}): matched existing project subtype #{targetSubtype.Id}.");
        }

        var subtypeTemplate = _snapshotExtractor.ExtractSingleSystemType(sourceDoc, sourceSubtypeId);
        if (subtypeTemplate is not null)
        {
            WriteParameters(sourceDoc, doc, targetSubtype, subtypeTemplate, elementIdCache);
        }

        try
        {
            assign(targetSubtype.Id);
            SmartConLogger.Debug(
                $"Subtype reference assigned ({slotName} = '{sourceSubtype.Name}', id=#{targetSubtype.Id}).");
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
        // assigned on the railing type. The position setter throws "The
        // rail has no primary/secondary hand rail" when the railing has no
        // such handrail at all — skip it then (stress test 2026-08-04:
        // false NotConverged noise on handrail-less railings).
        notConverged += SyncReferencedSubtype(sourceDoc, doc, source.TopRailType,
            typeof(TopRailType), null, "TopRailType", id => target.TopRailType = id, elementIdCache);
        if (IsValidId(source.PrimaryHandrailType))
        {
            notConverged += TrySetRailingMember("PrimaryHandRailPosition", () => target.PrimaryHandRailPosition = source.PrimaryHandRailPosition);
        }
        notConverged += SyncReferencedSubtype(sourceDoc, doc, source.PrimaryHandrailType,
            typeof(HandRailType), null, "PrimaryHandrailType", id => target.PrimaryHandrailType = id, elementIdCache);
        if (IsValidId(source.SecondaryHandrailType))
        {
            notConverged += TrySetRailingMember("SecondaryHandRailPosition", () => target.SecondaryHandRailPosition = source.SecondaryHandRailPosition);
        }
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

    /// <summary>
    /// FHV5: sync the wire settings — material / temperature rating /
    /// insulation / max size / conduit / neutral scalars. On Revit ≤2025 the
    /// members are an ownership-chain object graph (revitapidocs): temperature
    /// ratings belong to the assigned material; insulations and wire sizes
    /// belong to the assigned rating. A missing material is created from any
    /// existing one (same as "Duplicate" in the Revit electrical settings
    /// UI); missing rating/insulation/size/conduit objects are NOT created
    /// (their numeric content — ampacity, diameter — cannot be invented)
    /// and count as NotConverged with a Warn. Revit 2026+ replaced the graph
    /// with the FLAT Conductor* model (#233, <see cref="RevitConductorCompat"/>):
    /// every conductor kind is an independent document-level list with a
    /// Create() factory, so ALL missing conductor members (material/rating/
    /// insulation/size — the size carrying the source diameter) are created
    /// by name and the sync fully converges; only the conduit (still
    /// WireConduitType, no creation API) keeps the find-only degradation.
    /// </summary>
    private int SyncWireSettings(Document doc, Electrical.WireType source, Electrical.WireType target)
    {
        var notConverged = 0;

#if REVIT2026_OR_GREATER
        // #233: flat Conductor* model — WireMaterial/TemperatureRating/
        // Insulation are ElementIds into document-level lists, MaxSize is
        // the conductor-size NAME. Names are read through the per-kind
        // statics (the objects are not Element-derived); the source names
        // come from the SOURCE document, resolution/creation happens in the
        // target. Empty/invalid source members are no-ops (≤2025 semantics).
        notConverged += TrySetWireMember("WireMaterial", () =>
        {
            var sourceName = RevitConductorCompat.MaterialName(source.Document, source.WireMaterial);
            if (string.IsNullOrEmpty(sourceName)
                || string.Equals(
                    RevitConductorCompat.MaterialName(doc, target.WireMaterial),
                    sourceName, StringComparison.OrdinalIgnoreCase))
                return;

            target.WireMaterial = RevitConductorCompat.ResolveOrCreateMaterial(doc, sourceName!);
        });

        notConverged += TrySetWireMember("TemperatureRating", () =>
        {
            var sourceName = RevitConductorCompat.TemperatureRatingName(source.Document, source.TemperatureRating);
            if (string.IsNullOrEmpty(sourceName)
                || string.Equals(
                    RevitConductorCompat.TemperatureRatingName(doc, target.TemperatureRating),
                    sourceName, StringComparison.OrdinalIgnoreCase))
                return;

            target.TemperatureRating = RevitConductorCompat.ResolveOrCreateTemperatureRating(doc, sourceName!);
        });

        notConverged += TrySetWireMember("Insulation", () =>
        {
            var sourceName = RevitConductorCompat.InsulationName(source.Document, source.Insulation);
            if (string.IsNullOrEmpty(sourceName)
                || string.Equals(
                    RevitConductorCompat.InsulationName(doc, target.Insulation),
                    sourceName, StringComparison.OrdinalIgnoreCase))
                return;

            target.Insulation = RevitConductorCompat.ResolveOrCreateInsulation(doc, sourceName!);
        });

        notConverged += TrySetWireMember("MaxSize", () =>
        {
            var sourceName = source.MaxSize;
            if (string.IsNullOrEmpty(sourceName)
                || string.Equals(target.MaxSize, sourceName, StringComparison.OrdinalIgnoreCase))
                return;

            // WireType.MaxSize accepts only an EXISTING conductor-size name —
            // ensure the size first, carrying the source diameter (the
            // snapshot holds names only; the numeric content lives here).
            RevitConductorCompat.EnsureConductorSize(
                doc, sourceName!, RevitConductorCompat.SizeDiameter(source.Document, sourceName));
            target.MaxSize = sourceName;
        });
#else
        notConverged += TrySetWireMember("WireMaterial", () =>
        {
            var sourceName = source.WireMaterial?.Name;
            if (string.IsNullOrEmpty(sourceName)
                || string.Equals(target.WireMaterial?.Name, sourceName, StringComparison.OrdinalIgnoreCase))
                return;

            var material = FindByName<Electrical.WireMaterialType>(
                doc.Settings.ElectricalSetting?.WireMaterialTypes, sourceName!)
                ?? CreateWireMaterial(doc, sourceName!);
            if (material is null)
            {
                throw new InvalidOperationException(
                    $"wire material '{sourceName}' not found in the project and no base material exists to duplicate");
            }
            target.WireMaterial = material;
        });

        notConverged += TrySetWireMember("TemperatureRating", () =>
        {
            var sourceName = source.TemperatureRating?.Name;
            if (string.IsNullOrEmpty(sourceName)
                || string.Equals(target.TemperatureRating?.Name, sourceName, StringComparison.OrdinalIgnoreCase))
                return;

            var rating = target.WireMaterial?.TemperatureRatings
                ?.Cast<Electrical.TemperatureRatingType>()
                .FirstOrDefault(r => string.Equals(r.Name, sourceName, StringComparison.OrdinalIgnoreCase));
            if (rating is null)
            {
                throw new InvalidOperationException(
                    $"temperature rating '{sourceName}' not found under material '{target.WireMaterial?.Name}' in the project");
            }
            target.TemperatureRating = rating;
        });

        notConverged += TrySetWireMember("Insulation", () =>
        {
            var sourceName = source.Insulation?.Name;
            if (string.IsNullOrEmpty(sourceName)
                || string.Equals(target.Insulation?.Name, sourceName, StringComparison.OrdinalIgnoreCase))
                return;

            var insulation = target.TemperatureRating?.InsulationTypes
                ?.Cast<Electrical.InsulationType>()
                .FirstOrDefault(i => string.Equals(i.Name, sourceName, StringComparison.OrdinalIgnoreCase));
            if (insulation is null)
            {
                throw new InvalidOperationException(
                    $"insulation '{sourceName}' not found under temperature rating '{target.TemperatureRating?.Name}' in the project");
            }
            target.Insulation = insulation;
        });

        notConverged += TrySetWireMember("MaxSize", () =>
        {
            var sourceName = source.MaxSize?.Size;
            if (string.IsNullOrEmpty(sourceName)
                || string.Equals(target.MaxSize?.Size, sourceName, StringComparison.OrdinalIgnoreCase))
                return;

            var size = target.TemperatureRating?.WireSizes
                ?.Cast<Electrical.WireSize>()
                .FirstOrDefault(s => string.Equals(s.Size, sourceName, StringComparison.OrdinalIgnoreCase));
            if (size is null)
            {
                throw new InvalidOperationException(
                    $"wire size '{sourceName}' not found under temperature rating '{target.TemperatureRating?.Name}' in the project");
            }
            target.MaxSize = size;
        });
#endif

        notConverged += TrySetWireMember("Conduit", () =>
        {
            var sourceName = source.Conduit?.Name;
            if (string.IsNullOrEmpty(sourceName)
                || string.Equals(target.Conduit?.Name, sourceName, StringComparison.OrdinalIgnoreCase))
                return;

            var conduit = FindByName<Electrical.WireConduitType>(
                doc.Settings.ElectricalSetting?.WireConduitTypes, sourceName!);
            if (conduit is null)
            {
                throw new InvalidOperationException(
                    $"wire conduit '{sourceName}' not found in the project electrical settings");
            }
            target.Conduit = conduit;
        });

        notConverged += TrySetWireMember("NeutralMultiplier",
            () => target.NeutralMultiplier = source.NeutralMultiplier);
        notConverged += TrySetWireMember("NeutralRequired",
            () => target.NeutralRequired = source.NeutralRequired);

        return notConverged;
    }

    private static int TrySetWireMember(string memberName, Action write)
    {
        try
        {
            write();
            return 0;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Wire settings member '{memberName}' rejected: {ex.Message} " +
                "[Action: check the member in the project electrical settings — Manage > MEP Settings > " +
                "Electrical Conductor and Cable Settings (Revit 2026+) or Electrical Settings > Wiring (≤2025) — " +
                "and re-run the sync; the remaining members were applied]");
            return 1;
        }
    }

    private static T? FindByName<T>(IEnumerable? set, string name) where T : class
    {
        if (set is null) return null;
        foreach (var item in set)
        {
            if (item is not T typed) continue;
            var itemName = typed switch
            {
                Element element => element.Name,
                Electrical.WireConduitType conduit => conduit.Name,
                _ => null,
            };
            if (string.Equals(itemName, name, StringComparison.OrdinalIgnoreCase))
                return typed;
        }
        return null;
    }

#if !REVIT2026_OR_GREATER
    private static Electrical.WireMaterialType? CreateWireMaterial(Document doc, string name)
    {
        var setting = doc.Settings.ElectricalSetting;
        if (setting is null) return null;
        var baseMaterial = setting.WireMaterialTypes?.Cast<Electrical.WireMaterialType>().FirstOrDefault();
        if (baseMaterial is null) return null;
        SmartConLogger.Info(
            $"Wire material '{name}' missing in the project — created by duplicating '{baseMaterial.Name}' " +
            "(impedance factors follow the base; adjust in the electrical settings if they differ).");
        return setting.AddWireMaterialType(name, baseMaterial);
    }
#endif

    private static bool IsValidId(ElementId? id) =>
        id is not null && id != ElementId.InvalidElementId;

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
