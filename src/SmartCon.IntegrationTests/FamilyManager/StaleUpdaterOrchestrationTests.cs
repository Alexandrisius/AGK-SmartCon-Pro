using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.Import;
using SmartCon.FamilyManager.Services.Stale;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// #209 orchestration contracts (2026-08-11) for the REAL
/// <c>StaleFamilyUpdater</c> single-open family-document update cycle
/// (pre-verify → poke+merge via the borrowed source document →
/// arbitration/post-verify against the cached file hash). Catalog-side
/// seams are fakes (<c>StubFileResolver</c>, <c>InlineAwaitableEvent</c>,
/// <c>RecordingVersionWriter</c>); every Revit-side seam is real
/// (<c>RevitFamilyLoadService</c> through <c>CountingLoadService</c>,
/// <c>RevitFamilyVersionStore</c>, <c>RevitFamilySnapshotExtractor</c>,
/// <c>FamilyContentHasher</c>). Pins:
///  1. one update = one borrowed source open (provider contract);
///  2. a repeated update is a pre-verify skip (NO reload call, marker only);
///  3. source file open in the editor → honest failure, user document
///     untouched;
///  4. nested family open in the editor → guard failure, user edits intact;
///  5. post-verify mismatch → failure, NO marker (hash-spy negative);
///  6. a verified update writes the ES marker the marker-first import
///     resolution (<c>EmbeddedMarkerMatchResolver</c>) resolves to v2.
/// </summary>
public sealed class StaleUpdaterOrchestrationTests : RevitApiTest
{
    private const string ChildName = "SmartConOrchChild";
    private const string CatalogItemId = "orch-item";

    private string? _tempDir;
    private string? _template;
    private string? _childPath;
    private Document? _hostDoc;
    private List<Document>? _openDocs;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void Seed()
    {
        _template = SampleFiles.FindFamilyTemplate(Application);
        if (_template is null)
        {
            Skip.Test("Family templates (.rft) not found — cannot seed the orchestration contracts");
            return;
        }

        _tempDir = Path.Combine(Path.GetTempPath(), $"SmartConOrch_{Guid.NewGuid().ToString("N")}");
        Directory.CreateDirectory(_tempDir);
        _childPath = Path.Combine(_tempDir, ChildName + ".rfa");
        _openDocs = new List<Document>();

        CreateChildV1();
        SeedHostWithChild();
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void Cleanup()
    {
        try
        {
            if (_openDocs is not null)
            {
                foreach (var doc in _openDocs)
                {
                    try
                    {
                        if (doc.IsValidObject)
                        {
                            doc.Close(false);
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch
        {
        }

        try
        {
            if (_tempDir is not null && Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
        }
    }

    [Test]
    public async Task Orchestration_SingleUpdate_BorrowsOnePreOpenedSource()
    {
        MutateChildV2();

        var counting = CreateCountingService();
        var writer = new RecordingVersionWriter();
        var updater = CreateUpdater(counting, writer);

        var ok = await updater.UpdateFamilyAsync(CatalogItemId, overwriteParameterValues: true, CancellationToken.None);

        SmartConLogger.Info(
            $"Orchestration single-update: ok={ok} reloadCalls={counting.NestedReloadCalls} " +
            $"providerInvoked={counting.ProviderInvoked} providerReturnedDoc={counting.ProviderReturnedDocument} " +
            $"markerWrites={writer.Calls.Count}");

        await Assert.That(ok).IsTrue();
        await Assert.That(counting.NestedReloadCalls).IsEqualTo(1);
        await Assert.That(counting.ProviderInvoked).IsTrue();
        await Assert.That(counting.ProviderReturnedDocument).IsTrue();
        await Assert.That(writer.Calls.Count).IsEqualTo(1);
        await Assert.That(writer.Calls[0].VersionLabel).IsEqualTo("v2");
    }

    [Test]
    public async Task Orchestration_SecondUpdate_IsPreVerifySkipWithoutReload()
    {
        MutateChildV2();

        var counting = CreateCountingService();
        var writer = new RecordingVersionWriter();
        var updater = CreateUpdater(counting, writer);

        var first = await updater.UpdateFamilyAsync(CatalogItemId, overwriteParameterValues: true, CancellationToken.None);
        var second = await updater.UpdateFamilyAsync(CatalogItemId, overwriteParameterValues: true, CancellationToken.None);

        SmartConLogger.Info(
            $"Orchestration double-update: first={first} second={second} " +
            $"reloadCalls={counting.NestedReloadCalls} markerWrites={writer.Calls.Count}");

        await Assert.That(first).IsTrue();
        await Assert.That(second).IsTrue();
        await Assert.That(counting.NestedReloadCalls).IsEqualTo(1);
        await Assert.That(writer.Calls.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Orchestration_SourceFileOpenInEditor_FailsAndKeepsUserDocument()
    {
        MutateChildV2();

        // Simulate the user editing the version file: the updater must
        // refuse BEFORE touching it (the C1 guard) and the document must
        // stay open and valid.
        var userDoc = Application.OpenDocumentFile(_childPath!);
        _openDocs!.Add(userDoc);

        var counting = CreateCountingService();
        var writer = new RecordingVersionWriter();
        var updater = CreateUpdater(counting, writer);

        var ok = await updater.UpdateFamilyAsync(CatalogItemId, overwriteParameterValues: true, CancellationToken.None);

        SmartConLogger.Info(
            $"Orchestration source-open guard: ok={ok} reloadCalls={counting.NestedReloadCalls} " +
            $"markerWrites={writer.Calls.Count} userDocValid={userDoc.IsValidObject}");

        await Assert.That(ok).IsFalse();
        await Assert.That(counting.NestedReloadCalls).IsEqualTo(0);
        await Assert.That(writer.Calls.Count).IsEqualTo(0);
        await Assert.That(userDoc.IsValidObject).IsTrue();
    }

    [Test]
    public async Task Orchestration_NestedFamilyOpenInEditor_FailsAndKeepsUserEdits()
    {
        MutateChildV2();

        // Simulate the user editing the nested family itself (EditFamily):
        // the load-service Title guard must fail the update; the user's
        // editing session stays open.
        var nested = FindNestedFamily(_hostDoc!, ChildName);
        var editCopy = _hostDoc!.EditFamily(nested);
        _openDocs!.Add(editCopy);

        var counting = CreateCountingService();
        var writer = new RecordingVersionWriter();
        var updater = CreateUpdater(counting, writer);

        var ok = await updater.UpdateFamilyAsync(CatalogItemId, overwriteParameterValues: true, CancellationToken.None);

        SmartConLogger.Info(
            $"Orchestration nested-open guard: ok={ok} reloadCalls={counting.NestedReloadCalls} " +
            $"markerWrites={writer.Calls.Count} editCopyValid={editCopy.IsValidObject}");

        await Assert.That(ok).IsFalse();
        await Assert.That(writer.Calls.Count).IsEqualTo(0);
        await Assert.That(editCopy.IsValidObject).IsTrue();
    }

    [Test]
    public async Task Orchestration_PostVerifyMismatch_FailsAndWritesNoMarker()
    {
        // Negative contract (hash spy): the reload runs, but the post-verify
        // comparison fails → the update reports failure and NO marker is
        // written (a lying marker is impossible by construction).
        MutateChildV2();

        var counting = CreateCountingService();
        var writer = new RecordingVersionWriter();
        var updater = CreateUpdater(counting, writer, new CorruptEmbeddedVerifyHasher());

        var ok = await updater.UpdateFamilyAsync(CatalogItemId, overwriteParameterValues: true, CancellationToken.None);

        SmartConLogger.Info(
            $"Orchestration post-verify negative: ok={ok} reloadCalls={counting.NestedReloadCalls} " +
            $"markerWrites={writer.Calls.Count}");

        await Assert.That(ok).IsFalse();
        await Assert.That(counting.NestedReloadCalls).IsEqualTo(1);
        await Assert.That(writer.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Orchestration_VerifiedUpdate_MarkerFirstResolvesV2()
    {
        // Marker-first contract: the updater's marker lands as REAL
        // ExtensibleStorage on the embedded family; reading it back yields
        // the v2 label, and EmbeddedMarkerMatchResolver overrides a stale
        // hash-based dedup match (v1) to the marker version (v2).
        MutateChildV2();

        var context = new StubRevitContext(_hostDoc!);
        var store = new RevitFamilyVersionStore(new RevitTransactionService(context));
        var counting = CreateCountingService();
        var writer = new RecordingVersionWriter(_hostDoc, store);
        var updater = CreateUpdater(counting, writer);

        var ok = await updater.UpdateFamilyAsync(CatalogItemId, overwriteParameterValues: true, CancellationToken.None);
        await Assert.That(ok).IsTrue();

        var nested = FindNestedFamily(_hostDoc!, ChildName);
        var marker = store.ReadFromLoadedFamily(_hostDoc!, nested.Id);
        SmartConLogger.Info(
            $"Orchestration marker-first: marker={(marker is null ? "null" : marker.CatalogItemId + "@" + marker.VersionLabel)}");

        await Assert.That(marker).IsNotNull();
        await Assert.That(marker!.CatalogItemId).IsEqualTo(CatalogItemId);
        await Assert.That(marker.VersionLabel).IsEqualTo("v2");

        var overrode = EmbeddedMarkerMatchResolver.ResolveOverride(
            marker.CatalogItemId, marker.VersionLabel,
            dedupExistingCatalogItemId: CatalogItemId, dedupMatchedVersionLabel: "v1");
        await Assert.That(overrode).IsNotNull();
        await Assert.That(overrode!.Value.Status).IsEqualTo(FamilyBatchImportStatus.Duplicate);
        await Assert.That(overrode.Value.MatchedVersionLabel).IsEqualTo("v2");

        var noOverride = EmbeddedMarkerMatchResolver.ResolveOverride(
            marker.CatalogItemId, marker.VersionLabel,
            dedupExistingCatalogItemId: CatalogItemId, dedupMatchedVersionLabel: "v2");
        await Assert.That(noOverride).IsNull();
    }

    private void CreateChildV1()
    {
        var doc = Application.NewFamilyDocument(_template!);
        using (var tx = new Transaction(doc, "v1"))
        {
            tx.Start();
            doc.OwnerFamily?.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.Set(1);
#if REVIT2022_OR_GREATER
            doc.FamilyManager.AddParameter("P0", GroupTypeId.General, SpecTypeId.Number, false);
#else
            doc.FamilyManager.AddParameter("P0", BuiltInParameterGroup.PG_GENERAL, ParameterType.Number, false);
#endif
            doc.FamilyManager.NewType("TypeA");
            tx.Commit();
        }
        doc.SaveAs(_childPath!, new SaveAsOptions { OverwriteExistingFile = true });
        doc.Close(false);
    }

    private void SeedHostWithChild()
    {
        _hostDoc = Application.NewFamilyDocument(_template!);
        _openDocs!.Add(_hostDoc);
        using (var tx = new Transaction(_hostDoc, "Load child"))
        {
            tx.Start();
            if (!_hostDoc.LoadFamily(_childPath!, out _))
            {
                throw new InvalidOperationException("LoadFamily(child) returned false");
            }
            tx.Commit();
        }
    }

    private void MutateChildV2()
    {
        var doc = Application.OpenDocumentFile(_childPath!);
        try
        {
            using (var tx = new Transaction(doc, "v2"))
            {
                tx.Start();
#if REVIT2022_OR_GREATER
                doc.FamilyManager.AddParameter("P2", GroupTypeId.General, SpecTypeId.Number, false);
#else
                doc.FamilyManager.AddParameter("P2", BuiltInParameterGroup.PG_GENERAL, ParameterType.Number, false);
#endif
                tx.Commit();
            }
            doc.Save();
        }
        finally
        {
            doc.Close(false);
        }
    }

    private CountingLoadService CreateCountingService()
    {
        var context = new StubRevitContext(_hostDoc!);
        return new CountingLoadService(
            new RevitFamilyLoadService(context, new RevitTransactionService(context)));
    }

    private StaleFamilyUpdater CreateUpdater(
        IFamilyLoadService loadService,
        RecordingVersionWriter writer,
        IFamilyContentHasher? hasher = null)
    {
        var context = new StubRevitContext(_hostDoc!);
        return new StaleFamilyUpdater(
            loadService,
            new StubFileResolver(_childPath!, "v2"),
            new RevitFamilyVersionStore(new RevitTransactionService(context)),
            new NullFamilyManagerDialogService(),
            new InlineAwaitableEvent(),
            context,
            writer,
            new StubClock(),
            nestedSharedRepository: null,
            catalog: null,
            typeRepository: null,
            systemSyncOrchestrator: null,
            dependencyRepository: null,
            snapshotExtractor: new RevitFamilySnapshotExtractor(),
            contentHasher: hasher ?? new FamilyContentHasher());
    }

    private static Family FindNestedFamily(Document doc, string familyName)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(Family))
            .Cast<Family>()
            .First(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase));
    }
}
