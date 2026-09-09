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
public sealed partial class SystemTypeSyncService : ISystemTypeSyncService
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

}
