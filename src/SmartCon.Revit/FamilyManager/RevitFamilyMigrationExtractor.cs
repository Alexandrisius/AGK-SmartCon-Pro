using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Revit-bound implementation of <see cref="IFamilyMigrationExtractor"/>
/// (Issue #126). Opens one managed <c>.rfa</c> on the Revit main thread
/// via <see cref="IFamilyManagerAwaitableEvent"/>, extracts the snapshot
/// and closes the document without saving.
/// </summary>
/// <remarks>
/// Documents are opened and closed ONE AT A TIME — unlike batch import
/// there is no SaveAs phase, so holding documents open would only waste
/// memory. After every close a <see cref="IUiFreezeRecoveryService"/>
/// nudge is fired (workaround #96: DockablePane freezes after
/// OpenDocumentFile+Close cycles, REVIT-236376 / REVIT-237190).
/// </remarks>
public sealed class RevitFamilyMigrationExtractor : IFamilyMigrationExtractor
{
    private readonly IFamilyManagerAwaitableEvent _awaitableEvent;
    private readonly IFamilySnapshotExtractor _snapshotExtractor;
    private readonly IUiFreezeRecoveryService _freezeRecovery;

    public RevitFamilyMigrationExtractor(
        IFamilyManagerAwaitableEvent awaitableEvent,
        IFamilySnapshotExtractor snapshotExtractor,
        IUiFreezeRecoveryService freezeRecovery)
    {
        _awaitableEvent = awaitableEvent ?? throw new ArgumentNullException(nameof(awaitableEvent));
        _snapshotExtractor = snapshotExtractor ?? throw new ArgumentNullException(nameof(snapshotExtractor));
        _freezeRecovery = freezeRecovery ?? throw new ArgumentNullException(nameof(freezeRecovery));
    }

    public async Task<FamilyMigrationExtractResult> ExtractLoadableAsync(
        string absolutePath,
        CancellationToken ct = default)
    {
        var fileName = System.IO.Path.GetFileName(absolutePath);
        return await _awaitableEvent
            .RaiseAsync(appObj => ExtractOnMainThread(appObj, absolutePath, fileName), ct)
            .ConfigureAwait(false);
    }

    public async Task<FamilyMigrationExtractResult> ExtractLoadableWithGeometryAsync(
        string absolutePath,
        CancellationToken ct = default)
    {
        var fileName = System.IO.Path.GetFileName(absolutePath);
        return await _awaitableEvent
            .RaiseAsync(appObj => ExtractWithGeometryOnMainThread(appObj, absolutePath, fileName, ct), ct)
            .ConfigureAwait(false);
    }

    public async Task<FamilyMigrationExtractResult> ExtractSystemCategoryAsync(
        string absolutePath,
        CancellationToken ct = default)
    {
        var fileName = System.IO.Path.GetFileName(absolutePath);
        return await _awaitableEvent
            .RaiseAsync(appObj => ExtractSystemCategoryOnMainThread(appObj, absolutePath, fileName), ct)
            .ConfigureAwait(false);
    }

    private FamilyMigrationExtractResult ExtractSystemCategoryOnMainThread(
        object appObj, string absolutePath, string fileName)
    {
        Document? doc = null;
        try
        {
            var app = ((UIApplication)appObj).Application;
            doc = app.OpenDocumentFile(absolutePath);
            if (doc.IsFamilyDocument)
            {
                return FamilyMigrationExtractResult.Fail(
                    "expected a staged project document (IsFamilyDocument=true)");
            }

            // The staged .rvt is our own clean project: copied types + placed
            // instances of exactly ONE system category. Placed instances are
            // the domain truth — only what's on the view becomes catalog
            // types. Detection goes through SystemCategoryRegistry (the
            // product's canonical whitelist of system categories, the same
            // one AnalyzeActiveProject uses) — default project content
            // (levels, views, materials, curtain mullions) is excluded by
            // construction, no heuristics.
            //
            // #181: insulation mini-projects always contain a host pipe/duct
            // (CF-4720), and OST_PipeCurves precedes OST_PipeInsulations in
            // the registry — without the host filter the insulation staged
            // file would misdetect as "Трубы". Hosts are excluded from
            // detection exactly like in the import analysis.
            HashSet<ElementId>? insulatedHostIds = null;
            string? categoryName = null;
            BuiltInCategory? detectedCategory = null;
            foreach (var entry in SystemCategoryRegistry.Entries)
            {
                var instances = new FilteredElementCollector(doc)
                    .OfCategory(entry.Category)
                    .WhereElementIsNotElementType()
                    .AsEnumerable();

                if (InsulationHostFilter.IsInsulationHostCategory(entry.Category))
                {
                    insulatedHostIds ??= InsulationHostFilter.CollectInsulatedHostIds(doc);
                    instances = instances.Where(e => !insulatedHostIds.Contains(e.Id));
                }

                var instance = instances.FirstOrDefault();
                if (instance?.Category is not null)
                {
                    categoryName = instance.Category.Name;
                    detectedCategory = entry.Category;
                    break;
                }
            }

            // Fallback for legacy mini-projects staged BEFORE ADR-027 Phase 2
            // (copied WITHOUT placement) and for categories whose placement
            // degraded: detect the staged category by the copied types
            // through the same whitelist when no instance was found.
            if (detectedCategory is null)
            {
                foreach (var entry in SystemCategoryRegistry.Entries)
                {
                    var type = new FilteredElementCollector(doc)
                        .OfCategory(entry.Category)
                        .WhereElementIsElementType()
                        .FirstOrDefault();
                    if (type?.Category is not null)
                    {
                        categoryName = type.Category.Name;
                        detectedCategory = entry.Category;
                        break;
                    }
                }
            }

            // No staged instances/types = an empty mini-project (degenerate
            // but terminal): report an empty category so the caller writes
            // its "known missing" marker instead of retrying forever.
            if (detectedCategory is null)
            {
                var emptyStub = new FamilySnapshot(
                    FamilyName: System.IO.Path.GetFileNameWithoutExtension(fileName),
                    Category: string.Empty,
                    Parameters: [],
                    Types: [],
                    Geometry: new GeometryMetrics(0, []),
                    SharedNestedFamilyNames: [],
                    CategoryId: null);
                return FamilyMigrationExtractResult.Ok(emptyStub);
            }

            // ADR-056: full system snapshot (types + parameters + compound
            // structure + routing preferences) so the hash-v3 task can
            // recompute FHV3 system hashes from the staged project.
            var systemSnapshot = _snapshotExtractor
                .ExtractSystemCategoryFromStagedProject(doc, detectedCategory.Value);

            var stub = new FamilySnapshot(
                FamilyName: System.IO.Path.GetFileNameWithoutExtension(fileName),
                Category: categoryName ?? string.Empty,
                Parameters: [],
                Types: [],
                Geometry: new GeometryMetrics(0, []),
                SharedNestedFamilyNames: [],
                CategoryId: (int)detectedCategory.Value);
            return FamilyMigrationExtractResult.OkSystem(stub, systemSnapshot);
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"System category extract failed for '{fileName}': {ex.GetType().Name}: {ex.Message}");
            return FamilyMigrationExtractResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            CloseTemporaryDocument(doc);
        }
    }

    private FamilyMigrationExtractResult ExtractWithGeometryOnMainThread(
        object appObj, string absolutePath, string fileName, CancellationToken ct)
    {
        Document? doc = null;
        try
        {
            var app = ((UIApplication)appObj).Application;
            doc = app.OpenDocumentFile(absolutePath);
            if (!doc.IsFamilyDocument)
            {
                return FamilyMigrationExtractResult.Fail(
                    "not a family document (IsFamilyDocument=false)");
            }

            var snapshot = _snapshotExtractor.ExtractFromFamilyDocument(doc);

            // Geometry failure must not kill the snapshot — the caller
            // falls back to the pipeline's own extraction pass.
            IReadOnlyList<FamilyGeometryPerType>? geometry = null;
            try
            {
                geometry = _snapshotExtractor.ExtractGeometryPerType(doc, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                SmartConLogger.Debug(
                    $"Migration geometry extract failed for '{fileName}': {ex.GetType().Name}: {ex.Message}");
            }

            return FamilyMigrationExtractResult.Ok(snapshot, geometry);
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"Migration extract failed for '{fileName}': {ex.GetType().Name}: {ex.Message}");
            return FamilyMigrationExtractResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            CloseTemporaryDocument(doc);
        }
    }

    private FamilyMigrationExtractResult ExtractOnMainThread(
        object appObj, string absolutePath, string fileName)
    {
        Document? doc = null;
        try
        {
            var app = ((UIApplication)appObj).Application;
            doc = app.OpenDocumentFile(absolutePath);
            if (!doc.IsFamilyDocument)
            {
                return FamilyMigrationExtractResult.Fail(
                    "not a family document (IsFamilyDocument=false)");
            }

            var snapshot = _snapshotExtractor.ExtractFromFamilyDocument(doc);
            return FamilyMigrationExtractResult.Ok(snapshot);
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"Migration extract failed for '{fileName}': {ex.GetType().Name}: {ex.Message}");
            return FamilyMigrationExtractResult.Fail($"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            CloseTemporaryDocument(doc);
        }
    }

    private void CloseTemporaryDocument(Document? doc)
    {
        if (doc is not null)
        {
            try { doc.Close(false); } catch { /* already closed / invalid */ }
            // REVIT-237190: force synchronous COM cleanup of the temporary
            // document, bypassing the finalizer. Best-effort: on Revit
            // versions where Document is a managed wrapper (not a real COM
            // object) ReleaseComObject throws ArgumentException — ignored.
            try { System.Runtime.InteropServices.Marshal.ReleaseComObject(doc); } catch { }
        }
        // Workaround #96: force the WPF/render-thread resync after each
        // OpenDocumentFile+Close cycle. Single-space = invisible balloon.
        try { _freezeRecovery.Nudge(" "); } catch { /* best-effort */ }
    }
}
