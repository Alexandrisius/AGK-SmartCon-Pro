using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Revit implementation of <see cref="IMiniProjectRoutingSlimmingService"/>
/// (ADR-072, Phase 2b). Same file-level pattern as
/// <see cref="RevitMiniProjectActualizationService"/>: open → work →
/// <see cref="Document.Save()"/> in place → delete Revit backups → restore
/// read-only → close. The pre-slim routing extraction happens FIRST — a
/// legacy mini still carries the full routing (fittings included), which is
/// the backfill source for versions without section strings.
/// </summary>
public sealed class RevitMiniProjectRoutingSlimmingService : IMiniProjectRoutingSlimmingService
{
    private readonly IFamilyManagerAwaitableEvent _awaitableEvent;
    private readonly IFamilySnapshotExtractor _snapshotExtractor;
    private readonly ITransactionService _transactionService;

    public RevitMiniProjectRoutingSlimmingService(
        IFamilyManagerAwaitableEvent awaitableEvent,
        IFamilySnapshotExtractor snapshotExtractor,
        ITransactionService transactionService)
    {
        _awaitableEvent = awaitableEvent ?? throw new ArgumentNullException(nameof(awaitableEvent));
        _snapshotExtractor = snapshotExtractor ?? throw new ArgumentNullException(nameof(snapshotExtractor));
        _transactionService = transactionService ?? throw new ArgumentNullException(nameof(transactionService));
    }

    public async Task<MiniProjectSlimmingOutcome> SlimManagedFileAsync(
        string absolutePath, CancellationToken ct = default)
    {
        var fileName = Path.GetFileName(absolutePath);
        using var _scope = SmartConLogger.BeginScope(
            "MiniSlimming",
            ("Method", nameof(SlimManagedFileAsync)),
            ("FileName", fileName));

        return await _awaitableEvent
            .RaiseAsync(appObj => SlimOnMainThread(appObj, absolutePath), ct)
            .ConfigureAwait(false);
    }

    private MiniProjectSlimmingOutcome SlimOnMainThread(object appObj, string absolutePath)
    {
        if (!File.Exists(absolutePath))
        {
            SmartConLogger.Warn(
                $"Managed file missing: '{absolutePath}' [Action: версия помечена терминальным маркером (-2); " +
                "удалите запись через инструменты базы или восстановите файл]");
            return Empty(MiniProjectSlimmingStatus.Missing, "file not found");
        }

        Document? doc = null;
        var readOnlyCleared = false;
        try
        {
            // Same unwrap pattern as RevitMiniProjectActualizationService:
            // the production event delivers UIApplication, the DB-only test
            // host delivers ApplicationServices.Application — the
            // RevitAPIUI reference lives in a dedicated NoInlining method.
            var app = appObj as Autodesk.Revit.ApplicationServices.Application
                ?? UnwrapUIApplication(appObj);
            doc = app.OpenDocumentFile(absolutePath);

            // 1) Pre-slim extraction — the legacy mini still carries the
            // full routing (fittings included).
            var mepTypes = new FilteredElementCollector(doc)
                .OfClass(typeof(MEPCurveType))
                .Cast<MEPCurveType>()
                .ToList();
            var typeSnapshots = mepTypes
                .Select(t => _snapshotExtractor.ExtractSingleSystemType(doc, t.Id))
                .Where(s => s is not null)
                .Select(s => s!)
                .ToList();

            var loadableFamilies = new FilteredElementCollector(doc)
                .OfClass(typeof(Autodesk.Revit.DB.Family))
                .Cast<Autodesk.Revit.DB.Family>()
                .ToList();
            var hasFittingRules = typeSnapshots.Any(s => s.Routing?.Rules.Any(r =>
                r.PartName is not null
                && r.GroupType != (int)RoutingPreferenceRuleGroupType.Segments) == true);

            // Dirt is not only dragged fittings: the #254 residue (orphan
            // materials, digit-suffixed working copies) must also be healed
            // — a file whose fitting rules are already absent can still
            // carry it.
            var usedMaterialIds = CollectUsedMaterialIds(doc);
            var allMaterials = new FilteredElementCollector(doc)
                .OfClass(typeof(Material))
                .Cast<Material>()
                .ToList();
            var orphanMaterialIds = allMaterials
                .Where(m => !usedMaterialIds.Contains(m.Id.GetValue()))
                .Select(m => m.Id)
                .ToList();
            var orphanIdSet = orphanMaterialIds.Select(id => id.GetValue()).ToHashSet();
            // The #254 signature: a collision pair ('X' + 'X1') — the
            // family-internal orphan and the suffixed working copy. A
            // legitimately digit-ending name without its base in the
            // document ('Steel 45') is NOT dirt and is never renamed.
            var materialNames = allMaterials.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
            var suffixedPairBases = new HashSet<string>(StringComparer.Ordinal);
            // Pending renames computed UPFRONT (collectors inside the
            // transaction must not be trusted for name-availability after
            // the pending deletion): every suffixed material whose base
            // exists AND is an orphan (will be deleted below).
            var pendingRenames = new List<(ElementId Id, string BaseName)>();
            foreach (var m in allMaterials)
            {
                var baseName = TryGetSuffixedBase(m.Name, materialNames);
                if (baseName is null)
                    continue;
                suffixedPairBases.Add(baseName);
                var baseMaterial = allMaterials.First(x => string.Equals(x.Name, baseName, StringComparison.Ordinal));
                if (orphanIdSet.Contains(baseMaterial.Id.GetValue()))
                {
                    pendingRenames.Add((m.Id, baseName));
                }
            }
            if (suffixedPairBases.Count > 0)
            {
                SmartConLogger.Debug(
                    $"Suffixed pairs: [{string.Join(", ", suffixedPairBases)}], pending renames: {pendingRenames.Count}");
            }

            if (loadableFamilies.Count == 0 && !hasFittingRules
                && orphanMaterialIds.Count == 0 && suffixedPairBases.Count == 0)
            {
                // Already slim (new-format mini or a routing-less category):
                // there is NO full routing to backfill here — returning a
                // null snapshot protects the stored DB rules from being
                // overwritten by the slim state.
                SmartConLogger.Debug("Mini-project is already slim — nothing to do");
                return Empty(MiniProjectSlimmingStatus.AlreadySlim);
            }

            var preSlim = new SystemFamilySnapshot(doc.Title, 0, typeSnapshots);

            var attributes = File.GetAttributes(absolutePath);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(absolutePath, attributes & ~FileAttributes.ReadOnly);
                readOnlyCleared = true;
            }

            // 2) Slim: fitting routing rules removed (segments kept),
            // routing parameters forced to «Нет», fitting instances and
            // families deleted, orphan materials removed, suffixed working
            // copies renamed to the clean base name.
            var instancesDeleted = 0;
            var familiesDeleted = 0;
            var materialsDeleted = 0;
            var materialsRenamed = 0;
            var committed = _transactionService.RunInTransaction(doc, "SmartCon: Slim mini-project routing", d =>
            {
                foreach (var type in mepTypes)
                {
                    using var manager = type.RoutingPreferenceManager;
                    if (manager is not null)
                    {
                        foreach (RoutingPreferenceRuleGroupType group in Enum.GetValues(typeof(RoutingPreferenceRuleGroupType)))
                        {
                            if (group is RoutingPreferenceRuleGroupType.Undefined
                                or RoutingPreferenceRuleGroupType.Segments)
                                continue;
                            for (var i = manager.GetNumberOfRules(group) - 1; i >= 0; i--)
                            {
                                try { manager.RemoveRule(group, i); }
                                catch (Exception ex) { SmartConLogger.Debug($"RemoveRule({group}[{i}]) skipped: {ex.Message}"); }
                            }
                        }
                    }
                    else
                    {
                        foreach (Parameter p in type.Parameters)
                        {
                            if (RoutingDrivingParameters.TryGetRoutingParam(p) is null || p.IsReadOnly)
                                continue;
                            try { p.Set(ElementId.InvalidElementId); }
                            catch (Exception ex) { SmartConLogger.Debug($"Routing param clear skipped: {ex.Message}"); }
                        }
                    }
                }

                // Fitting instances first — their families cannot be deleted
                // while placed instances reference them.
                var fittingInstances = new FilteredElementCollector(d)
                    .OfClass(typeof(FamilyInstance))
                    .WhereElementIsNotElementType()
                    .Select(e => e.Id)
                    .ToList();
                if (fittingInstances.Count > 0)
                {
                    instancesDeleted += d.Delete(fittingInstances).Count;
                }

                var familyIds = loadableFamilies.Select(f => f.Id).ToList();
                if (familyIds.Count > 0)
                {
                    familiesDeleted += d.Delete(familyIds).Count;
                }

                if (orphanMaterialIds.Count > 0)
                {
                    var deleted = d.Delete(orphanMaterialIds);
                    materialsDeleted += deleted.Count;
                    if (deleted.Count < orphanMaterialIds.Count)
                    {
                        var survived = orphanMaterialIds.Where(id => !deleted.Contains(id))
                            .Select(id => doc.GetElement(id)?.Name ?? id.ToString());
                        SmartConLogger.Debug(
                            $"Orphan material delete partial: {deleted.Count}/{orphanMaterialIds.Count}, survived: [{string.Join(", ", survived)}]");
                    }
                }

                // Cascade orphans: materials referenced ONLY by the deleted
                // fitting instances/families became unused by the deletions
                // above — rescan inside the same transaction (the pre-scan
                // cannot see them).
                var cascadeOrphanIds = new FilteredElementCollector(d)
                    .OfClass(typeof(Material))
                    .Cast<Material>()
                    .Where(m => !CollectUsedMaterialIds(d).Contains(m.Id.GetValue()))
                    .Select(m => m.Id)
                    .ToList();
                if (cascadeOrphanIds.Count > 0)
                {
                    materialsDeleted += d.Delete(cascadeOrphanIds).Count;
                }
                materialsRenamed += ApplyPendingRenames(d, pendingRenames);
            });

            // Audit M5: a rolled-back slim transaction means the file is
            // unchanged — saving it and returning Slimmed would mark the
            // still-dirty mini as processed forever (the task maps Slimmed
            // to the terminal marker 1). The pre-slim snapshot was already
            // captured, so the backfill data stays valid either way.
            if (!committed)
            {
                SmartConLogger.Warn(
                    $"Slim transaction rolled back for '{Path.GetFileName(absolutePath)}' — the file is unchanged. " +
                    "[Action: версия помечена терминальным маркером пропуска (-1); проверьте, что файл не открыт в другом Revit и доступен на запись]");
                return Empty(MiniProjectSlimmingStatus.Failed, "slim transaction rolled back");
            }

            doc.Save();
            var backupsDeleted = DeleteRevitBackups(absolutePath);
            SmartConLogger.Info(
                $"Mini-project slimmed: fittingRules cleared, instances={instancesDeleted}, " +
                $"families={familiesDeleted}, orphanMaterials={materialsDeleted}, renamed={materialsRenamed}, " +
                $"backups={backupsDeleted}");

            return new MiniProjectSlimmingOutcome(
                MiniProjectSlimmingStatus.Slimmed,
                preSlim,
                instancesDeleted,
                familiesDeleted,
                materialsDeleted,
                materialsRenamed,
                backupsDeleted);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Slimming '{Path.GetFileName(absolutePath)}' failed: {ex.Message} " +
                "[Action: версия помечена терминальным маркером пропуска (-1); проверьте, что файл не открыт в другом Revit и доступен на запись]");
            return Empty(MiniProjectSlimmingStatus.Failed, ex.Message);
        }
        finally
        {
            if (doc is not null)
            {
                try { doc.Close(false); }
                catch (Exception ex) { SmartConLogger.Debug($"doc.Close skipped: {ex.Message}"); }
            }
            if (readOnlyCleared)
            {
                try
                {
                    File.SetAttributes(absolutePath, File.GetAttributes(absolutePath) | FileAttributes.ReadOnly);
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn(
                        $"Failed to restore the read-only attribute on '{Path.GetFileName(absolutePath)}': {ex.Message} " +
                        "[Action: выставьте атрибут «Только чтение» вручную — managed storage должен оставаться read-only (I-16)]");
                }
            }
        }
    }

    /// <summary>
    /// Every material id referenced by geometry or ElementId type
    /// parameters — the orphan detector (the #254 duplicate is NOT
    /// referenced; the suffixed working copy survives via the segment).
    /// </summary>
    private static HashSet<long> CollectUsedMaterialIds(Document doc)
    {
        var used = new HashSet<long>();
        foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType())
        {
            foreach (var id in e.GetMaterialIds(true)) used.Add(id.GetValue());
        }
        foreach (var e in new FilteredElementCollector(doc).WhereElementIsElementType())
        {
            foreach (var id in e.GetMaterialIds(true)) used.Add(id.GetValue());
            if (e is not ElementType et) continue;
            foreach (Parameter p in et.Parameters)
            {
                if (p.StorageType == StorageType.ElementId && p.HasValue)
                {
                    used.Add(p.AsElementId().GetValue());
                }
            }
        }
        // Segment.MaterialId is a property, not geometry — GetMaterialIds
        // never reports it, and without this the segment's own material
        // would be "orphan"-deleted (the mini's core reference content).
        foreach (var segment in new FilteredElementCollector(doc).OfClass(typeof(Segment)).Cast<Segment>())
        {
            if (segment.MaterialId is not null && segment.MaterialId != ElementId.InvalidElementId)
            {
                used.Add(segment.MaterialId.GetValue());
            }
        }
        return used;
    }

    /// <summary>
    /// The base name of a #254 collision pair member: <paramref name="name"/>
    /// with its trailing digits stripped, but only when that base ALSO
    /// exists as a material in the document (the pair signature). A
    /// standalone digit-ending name yields <c>null</c>.
    /// </summary>
    private static string? TryGetSuffixedBase(string name, HashSet<string> allNames)
    {
        if (name.Length < 2 || !char.IsDigit(name[^1]))
            return null;
        var cut = name.Length;
        while (cut > 0 && char.IsDigit(name[cut - 1])) cut--;
        if (cut == name.Length || cut == 0)
            return null;
        var baseName = name.Substring(0, cut);
        return allNames.Contains(baseName) ? baseName : null;
    }

    /// <summary>
    /// Executes the upfront-computed renames (<c>'X1'</c> → <c>'X'</c>)
    /// after the orphan base was deleted in the same transaction.
    /// </summary>
    private static int ApplyPendingRenames(Document doc, List<(ElementId Id, string BaseName)> pendingRenames)
    {
        var renamed = 0;
        foreach (var (id, baseName) in pendingRenames)
        {
            if (doc.GetElement(id) is not Material material)
                continue;
            try
            {
                material.Name = baseName;
                renamed++;
            }
            catch (Exception ex)
            {
                SmartConLogger.Debug($"Material rename '{material.Name}' → '{baseName}' skipped: {ex.Message}");
            }
        }
        return renamed;
    }

    private static MiniProjectSlimmingOutcome Empty(MiniProjectSlimmingStatus status, string? error = null)
        => new(status, null, 0, 0, 0, 0, 0, error);

    /// <summary>
    /// Unwraps the production UIApplication to the inner application object.
    /// NoInlining + isolated here: the DB-only integration test host has no
    /// RevitAPIUI on disk and must never JIT a method referencing it.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static Autodesk.Revit.ApplicationServices.Application UnwrapUIApplication(object appObj)
    {
        if (appObj is not UIApplication uiApp)
        {
            throw new InvalidOperationException(
                $"Unexpected Revit app context type: {appObj.GetType().FullName}");
        }
        return uiApp.Application;
    }

    /// <summary>
    /// Deletes the Revit backup files (<c>name.NNNN.rvt</c>) that
    /// <see cref="Document.Save()"/> leaves next to the managed file
    /// (same hygiene rule as the marker actualization).
    /// </summary>
    private static int DeleteRevitBackups(string absolutePath)
    {
        var directory = Path.GetDirectoryName(absolutePath);
        if (string.IsNullOrEmpty(directory)) return 0;
        var baseName = Path.GetFileNameWithoutExtension(absolutePath);

        var deleted = 0;
        foreach (var candidate in Directory.EnumerateFiles(directory, baseName + ".*.rvt"))
        {
            var suffix = Path.GetFileName(candidate).AsSpan(baseName.Length);
            if (suffix.Length != 9 || suffix[0] != '.'
                || !int.TryParse(suffix.Slice(1, 4).ToString(), out _))
                continue;
            try
            {
                File.SetAttributes(candidate, FileAttributes.Normal);
                File.Delete(candidate);
                deleted++;
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"Failed to delete Revit backup '{Path.GetFileName(candidate)}': {ex.Message} " +
                    "[Action: удалите файл вручную — в папке версии должен оставаться один .rvt]");
            }
        }
        return deleted;
    }
}
