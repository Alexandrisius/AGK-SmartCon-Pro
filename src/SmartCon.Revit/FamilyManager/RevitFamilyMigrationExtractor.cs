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
            string? categoryName = null;
            foreach (var entry in SystemCategoryRegistry.Entries)
            {
                var instance = new FilteredElementCollector(doc)
                    .OfCategory(entry.Category)
                    .WhereElementIsNotElementType()
                    .FirstOrDefault();
                if (instance?.Category is not null)
                {
                    categoryName = instance.Category.Name;
                    break;
                }
            }

            // Phase-2 registry categories are copied WITHOUT placement
            // (PlacementHandler = null → placed=0, e.g. floors/roofs/stairs) —
            // fall back to the copied types through the same whitelist.
            if (categoryName is null)
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
                        break;
                    }
                }
            }

            // No staged instances/types = an empty mini-project (degenerate
            // but terminal): report an empty category so the caller writes
            // its "known missing" marker instead of retrying forever.
            var snapshot = new FamilySnapshot(
                FamilyName: System.IO.Path.GetFileNameWithoutExtension(fileName),
                Category: categoryName ?? string.Empty,
                Parameters: [],
                Types: [],
                Geometry: new GeometryMetrics(0, []),
                SharedNestedFamilyNames: []);
            return FamilyMigrationExtractResult.Ok(snapshot);
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
