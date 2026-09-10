using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.Import;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.FamilyManager.Services;

public sealed partial class FamilyImportPreparationService
{
    private async Task<PreparedFamilyItem> PrepareSystemCategoryAsync(
        CategoryAnalysis analysis, CancellationToken ct)
    {
        SmartConLogger.Debug($"Preparing system category: {analysis.DisplayName}");

        var typeUniqueIds = analysis.Types.Select(t => t.UniqueId).ToList();
        var builtInCategory = analysis.Category;

        IReadOnlyList<FamilyDependencyDescriptor>? routingDependencies = null;
        string? miniCatalogItemId = null;
        var unsubstitutedMiniRouting = false;
        var snapshot = await _awaitableEvent
            .RaiseAsync(app =>
            {
                var activeDoc = _revitContext.GetDocument();
                var extracted = _snapshotExtractor.ExtractFromProject(
                    activeDoc, typeUniqueIds, builtInCategory);
                // ADR-066 (E1): same Revit-thread roundtrip — routing rules
                // resolve to live families whose identities (UniqueId) drive
                // the dependency auto-import below.
                routingDependencies = _dependencyCollector.CollectRoutingDependencies(activeDoc, extracted);
                // ADR-072 (plan item 4): same roundtrip — the mini marker
                // tells whether the extracted routing is the SLIM one
                // (reimport from a mini-project) and which catalog item owns
                // the stored routing rules.
                if (_miniProjectMarker?.IsMiniProject(activeDoc) == true)
                {
                    miniCatalogItemId = _miniProjectMarker.ReadCatalogItemId(activeDoc);
                }
                return extracted;
            }, ct)
            .ConfigureAwait(false);

        // ADR-072 (plan item 4, World B): reimport from a slim mini-project.
        // FHV20 dropped ROUTING from the hash, so the substitution no longer
        // affects dedup/sections — its remaining purpose is the SEED source
        // of the item-level routing tables (RoutingRuleWriter reads the
        // final snapshot): legacy items whose full routing lives in the
        // frozen V34 rows get it restored here; when nothing is stored the
        // slim mini state must NOT be seeded (flagged below, audit M11).
        if (miniCatalogItemId is not null && _routingRuleRepository is not null)
        {
            var (substituted, substitutedSnapshot) = await SubstituteRoutingFromDbAsync(snapshot, miniCatalogItemId, ct)
                .ConfigureAwait(false);
            snapshot = substitutedSnapshot;
            // Audit M11: a mini reimport whose routing could NOT be
            // substituted (no stored rows anywhere) carries the slim mini
            // state — flag it so the import never seeds it as catalog truth.
            unsubstitutedMiniRouting = !substituted;
        }

        var hash = _contentHasher.ComputeForSystem(snapshot);
        // #249 (Phase 2): per-type content hashes — the DB writer persists
        // them to family_type_hashes without re-opening the staged file.
        var perTypeHashes = _contentHasher.ComputePerTypeHashesForSystem(snapshot)
            ?.Select(FamilyTypeHashEntry.ForSystemType)
            .ToList();
        // #249 (Phase 4): canonical sections — persisted to
        // section_hashes/section_strings at import.
        var systemSections = _contentHasher.ComputeSectionsForSystem(snapshot);
        var displayName = analysis.DisplayName;
        var normalizedName = FamilyNameNormalizer.Normalize(displayName);

        var dedupResult = await Task.Run(
            () => _dedupService.CheckAsync(normalizedName, hash, "system", (int)builtInCategory, ct),
            ct).ConfigureAwait(false);

        SmartConLogger.Info(
            $"System category prepared: '{displayName}', hash={hash?.HexString ?? "null"}, " +
            $"status={dedupResult.Status}");

        var sourceTypes = analysis.Types
            .Select(t => new FamilySourceTypeInfo(t.UniqueId, t.Name, displayName, (int)builtInCategory, t.FamilyName, t.FamilyKey))
            .ToList();

        var source = new FamilyImportSource.SystemSource(
            DisplayName: displayName,
            CategoryId: (int)builtInCategory,
            TypeUniqueIds: typeUniqueIds,
            TypeNames: analysis.Types.Select(t => t.Name).ToList(),
            // #183: parallel family-name list so the staged import persists
            // family_types.family_name and the sync matches by (family, name).
            TypeFamilyNames: analysis.Types.Select(t => t.FamilyName).ToList(),
            // #190 (ADR-064): parallel family-key list → family_types.family_key.
            TypeFamilyKeys: analysis.Types.Select(t => t.FamilyKey).ToList());

        return new PreparedFamilyItem(
            SourcePath: $"system://{displayName}",
            DisplayName: displayName,
            RevitMajorVersion: GetRevitMajorVersion(),
            ContentHash: hash,
            LoadableSnapshot: null,
            SystemSnapshot: snapshot,
            ErrorMessage: null,
            Source: source,
            SourceTypes: sourceTypes,
            FamilySource: "system",
            Status: dedupResult.Status,
            ExistingCatalogItemId: dedupResult.ExistingCatalogItemId,
            ExistingVersionLabel: dedupResult.ExistingVersionLabel,
            MatchedVersionLabel: dedupResult.HashMatch?.MatchedVersionLabel,
            IsCrossNameDuplicate: dedupResult.IsCrossNameDuplicate,
            MatchedItemName: dedupResult.HashMatch?.MatchedItemName,
            RoutingDependencies: routingDependencies,
            PerTypeHashes: perTypeHashes,
            Sections: systemSections,
            UnsubstitutedMiniRouting: unsubstitutedMiniRouting);
    }

    /// <summary>
    /// ADR-072 World B: reimport from a slim mini-project — replaces each
    /// type's extracted (slim) routing with the catalog's item-level routing
    /// links (V37), falling back to the current version's V34 rows for pre-
    /// World-B versions. Post-FHV20 this no longer drives dedup (ROUTING
    /// left the hash) — it feeds the item-table SEED and the
    /// <c>UnsubstitutedMiniRouting</c> guard: <c>Substituted=false</c> when
    /// no stored rows exist, no type matched (routing-less type / data
    /// drift), or the DB read failed — the slim mini routing then stays in
    /// the snapshot and must never become catalog truth (audit M11).
    /// </summary>
    private async Task<(bool Substituted, SystemFamilySnapshot Snapshot)> SubstituteRoutingFromDbAsync(
        SystemFamilySnapshot snapshot, string catalogItemId, CancellationToken ct)
    {
        try
        {
            var (rules, settings) = await _routingRuleRepository!
                .HasAnyForItemAsync(catalogItemId, ct).ConfigureAwait(false)
                ? await _routingRuleRepository!.ReadForItemAsync(catalogItemId, ct).ConfigureAwait(false)
                : !await _routingRuleRepository!.HasRulesForCurrentVersionAsync(catalogItemId, ct)
                        .ConfigureAwait(false)
                    ? default
                    : await _routingRuleRepository!.ReadForCurrentVersionAsync(catalogItemId, ct).ConfigureAwait(false);
            if (rules is null)
            {
                SmartConLogger.Debug(
                    "Reimport from mini-project: no stored routing rows (pre-V34 version) — " +
                    "the mini-project routing is used as-is");
                return (false, snapshot);
            }

            var substituted = 0;
            var types = snapshot.Types.Select(t =>
            {
                var dbRouting = RoutingRuleRecordMapper.ToSnapshot(
                    t.Name, t.FamilyKey ?? string.Empty, rules, settings!);
                if (dbRouting is null)
                    return t;
                substituted++;
                // FHV21 (owner decision 2026-09-01): SEGMENT rules stay with
                // the MINI — they are versioned mini content entering the
                // SEGMENTS hash section and the per-version store. Only the
                // FITTING groups substitute from the catalog (slim minis
                // lost them by design). The DB's stored Segments rows are
                // legacy and must not shadow the mini's own.
                var miniSegmentRules = t.Routing?.Rules
                    .Where(r => r.GroupType == (int)RoutingManagerGroup.Segments)
                    .ToList() ?? [];
                var mergedRules = dbRouting.Rules
                    .Where(r => r.GroupType != (int)RoutingManagerGroup.Segments)
                    .Concat(miniSegmentRules)
                    .ToList();
                return t with { Routing = dbRouting with { Rules = mergedRules } };
            }).ToList();

            SmartConLogger.Info(
                $"Reimport from mini-project: routing substituted from the catalog DB for {substituted} type(s)");
            return (substituted > 0, snapshot with { Types = types });
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Routing substitution from the catalog DB failed: {ex.Message} " +
                "[Action: трассировка реимпорта взята из мини-проекта; проверьте базу каталога и повторите импорт]");
            return (false, snapshot);
        }
    }
}
