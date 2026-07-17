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
            if (doc is not null)
            {
                try { doc.Close(false); } catch { /* already closed / invalid */ }
            }
            // Workaround #96: force the WPF/render-thread resync after each
            // OpenDocumentFile+Close cycle. Single-space = invisible balloon.
            try { _freezeRecovery.Nudge(" "); } catch { /* best-effort */ }
        }
    }
}
