using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.FamilyManager.Services.Stale;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// Content-verification contracts for the stale CHECK (#180, owner decision
/// 2026-08-12), built on the group-propagation probe
/// (<see cref="GroupPropagationProbeTests"/>, same day — poke + doc-to-doc
/// merge did NOT propagate the parameter group although the merge itself
/// landed: groups are physically non-transferable). The rule under test —
/// the check proves CONTENT (the unified FHV10 hash, <see cref="EmbeddedContentVerifier"/>)
/// whenever the marker alone cannot speak:
/// <list type="number">
/// <item>NO marker (freshly imported assemblies carry none — the import
/// writes no markers) → one slow content proof: match → not stale and the
/// marker is healed (best-effort); differs/indeterminate → stays
/// stale.</item>
/// <item>Stale marker (label != current) → stale WITHOUT opening any
/// document (zero extractions); «Обновить» reconciles (pre-verify skip →
/// marker). A group-only label drift can neither be confirmed nor fixed by
/// a content check — merges never propagate groups.</item>
/// <item>Marker == current → RE-VERIFY against the file: match → not
/// stale; DIFFERS → the copy was edited locally after the marker was
/// written → stale (ContentDrift), «Обновить» restores the catalog
/// content; indeterminate → the marker stands. Applies uniformly to family
/// documents (embedded nested) and project documents (loaded
/// families).</item>
/// </list>
/// Also pins: one OpenDocumentFile per version FILE per check run
/// (duplicate items resolving to the same file share the cached hash).
/// </summary>
public sealed class StaleCheckContentFallbackTests : RevitApiTest
{
    private const string ChildName = "SmartConCheckChild";
    private const string CatalogItemId = "check-item";

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
            Skip.Test("Family templates (.rft) not found — cannot seed the content-fallback contracts");
            return;
        }

        _tempDir = Path.Combine(Path.GetTempPath(), $"SmartConCheck_{Guid.NewGuid().ToString("N")}");
        Directory.CreateDirectory(_tempDir);
        _childPath = Path.Combine(_tempDir, ChildName + ".rfa");
        _openDocs = new List<Document>();

        var doc = Application.NewFamilyDocument(_template);
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

        _hostDoc = Application.NewFamilyDocument(_template);
        _openDocs.Add(_hostDoc);
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
    public async Task Check_MarkerLess_ContentMatches_NotStale_AndHealsMarker()
    {
        var (detector, writer, store, _) = CreateDetector(currentLabel: "v1");

        var results = await detector.CheckCategoryAsync(null, _hostDoc!, CancellationToken.None);

        var verdict = results.First(r => string.Equals(r.FamilyName, ChildName, StringComparison.OrdinalIgnoreCase));
        SmartConLogger.Info(
            $"CheckFallback no-marker/match: IsStale={verdict.IsStale} Reason={verdict.Reason} healed={writer.Calls.Count}");
        await Assert.That(verdict.IsStale).IsFalse();
        await Assert.That(writer.Calls.Count).IsEqualTo(1);
        await Assert.That(writer.Calls[0].VersionLabel).IsEqualTo("v1");

        var nested = FindNestedFamily(_hostDoc!, ChildName);
        var marker = store.ReadFromLoadedFamily(_hostDoc!, nested.Id);
        await Assert.That(marker).IsNotNull();
        await Assert.That(marker!.VersionLabel).IsEqualTo("v1");
    }

    [Test]
    public async Task Check_MarkerLess_ContentDiffers_StaysStale()
    {
        BumpChildToV2();
        var (detector, writer, _, _) = CreateDetector(currentLabel: "v2");

        var results = await detector.CheckCategoryAsync(null, _hostDoc!, CancellationToken.None);

        var verdict = results.First(r => string.Equals(r.FamilyName, ChildName, StringComparison.OrdinalIgnoreCase));
        SmartConLogger.Info(
            $"CheckFallback no-marker/diff: IsStale={verdict.IsStale} Reason={verdict.Reason} healed={writer.Calls.Count}");
        await Assert.That(verdict.IsStale).IsTrue();
        await Assert.That(writer.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Check_MarkerLess_GroupOnlyDiff_NotStale_AndHealsMarker()
    {
        // Outcome-B contract (probe 2026-08-12): v2 differs from v1 ONLY by
        // the parameter group (P0 General → Data) — FHV10 never hashes
        // groups, so the file content is identical by definition. The
        // marker-less check clears the badge and heals the marker: a group
        // can NEVER land in an embedded definition via any merge, so no
        // content check or update could ever reconcile such a drift
        // physically — and with FHV10 it no longer needs to.
        // (ADR-068 addendum).
        BumpChildToGroupOnlyV2();
        var (detector, writer, _, _) = CreateDetector(currentLabel: "v2");

        var results = await detector.CheckCategoryAsync(null, _hostDoc!, CancellationToken.None);

        var verdict = results.First(r => string.Equals(r.FamilyName, ChildName, StringComparison.OrdinalIgnoreCase));
        SmartConLogger.Info(
            $"CheckFallback no-marker/group-only: IsStale={verdict.IsStale} Reason={verdict.Reason} healed={writer.Calls.Count}");
        await Assert.That(verdict.IsStale).IsFalse();
        await Assert.That(writer.Calls.Count).IsEqualTo(1);
        await Assert.That(writer.Calls[0].VersionLabel).IsEqualTo("v2");
    }

    [Test]
    public async Task Check_OlderMarker_StaysStale_WithoutContentVerify()
    {
        // Outcome-B rule: a STALE MARKER (label != current) is reported
        // stale WITHOUT opening any document — even when the embedded
        // content already matches the current version (the crane case:
        // reloaded to v2 below, marker plants v1). «Обновить» is the
        // reconciliation path (pre-verify skip → marker), not the check.
        BumpChildToV2();
        var context = new StubRevitContext(_hostDoc!);
        var reloaded = new RevitFamilyLoadService(context, new RevitTransactionService(context))
            .ReloadNestedInFamilyDocument(_childPath!, ChildName, overwriteParameterValues: true);
        if (!reloaded.Success)
        {
            throw new InvalidOperationException($"Nested reload to v2 failed: {reloaded.ErrorMessage}");
        }
        var (detector, writer, store, spy) = CreateDetector(currentLabel: "v2");
        var nested = FindNestedFamily(_hostDoc!, ChildName);
        store.WriteToLoadedFamily(_hostDoc!, nested.Id,
            new FamilyVersion(1, CatalogItemId, "v1", DateTimeOffset.UtcNow, CurrentRevitMajor));

        var results = await detector.CheckCategoryAsync(null, _hostDoc!, CancellationToken.None);

        var verdict = results.First(r => string.Equals(r.FamilyName, ChildName, StringComparison.OrdinalIgnoreCase));
        SmartConLogger.Info(
            $"CheckFallback older-marker/drift: IsStale={verdict.IsStale} Reason={verdict.Reason} " +
            $"healed={writer.Calls.Count} extractions={spy.FamilyDocumentExtractions}");
        await Assert.That(verdict.IsStale).IsTrue();
        await Assert.That(verdict.Reason).IsEqualTo(StaleReason.VersionMismatch);
        await Assert.That(writer.Calls.Count).IsEqualTo(0);
        await Assert.That(spy.FamilyDocumentExtractions).IsEqualTo(0);
    }

    [Test]
    public async Task Check_CurrentMarker_ContentMatches_Reverified()
    {
        // #180: even a CURRENT marker is re-verified against the file
        // (the user may have edited the copy after the marker was written).
        // Content matches → not stale, nothing written; the proof costs
        // one EditFamily + one OpenDocumentFile (spy = 2 extractions).
        var (detector, writer, store, spy) = CreateDetector(currentLabel: "v1");
        var nested = FindNestedFamily(_hostDoc!, ChildName);
        store.WriteToLoadedFamily(_hostDoc!, nested.Id,
            new FamilyVersion(1, CatalogItemId, "v1", DateTimeOffset.UtcNow, CurrentRevitMajor));

        var results = await detector.CheckCategoryAsync(null, _hostDoc!, CancellationToken.None);

        var verdict = results.First(r => string.Equals(r.FamilyName, ChildName, StringComparison.OrdinalIgnoreCase));
        SmartConLogger.Info(
            $"CheckFallback current-marker/match: IsStale={verdict.IsStale} Reason={verdict.Reason} " +
            $"healed={writer.Calls.Count} extractions={spy.FamilyDocumentExtractions}");
        await Assert.That(verdict.IsStale).IsFalse();
        await Assert.That(writer.Calls.Count).IsEqualTo(0);
        await Assert.That(spy.FamilyDocumentExtractions).IsEqualTo(2);
    }

    [Test]
    public async Task Check_CurrentMarker_ContentDrift_Stale()
    {
        // #180 core scenario: the marker says v1 == current v1, but the
        // embedded copy was EDITED LOCALLY afterwards (a scratch parameter
        // added via edit-in-place). A marker-only check would call this
        // family healthy forever; the content proof must see the drift.
        var (detector, writer, store, spy) = CreateDetector(currentLabel: "v1");
        var nested = FindNestedFamily(_hostDoc!, ChildName);
        store.WriteToLoadedFamily(_hostDoc!, nested.Id,
            new FamilyVersion(1, CatalogItemId, "v1", DateTimeOffset.UtcNow, CurrentRevitMajor));
        EditEmbeddedChildLocally();

        var results = await detector.CheckCategoryAsync(null, _hostDoc!, CancellationToken.None);

        var verdict = results.First(r => string.Equals(r.FamilyName, ChildName, StringComparison.OrdinalIgnoreCase));
        SmartConLogger.Info(
            $"CheckFallback current-marker/drift: IsStale={verdict.IsStale} Reason={verdict.Reason} " +
            $"healed={writer.Calls.Count} extractions={spy.FamilyDocumentExtractions}");
        await Assert.That(verdict.IsStale).IsTrue();
        await Assert.That(verdict.Reason).IsEqualTo(StaleReason.ContentDrift);
        await Assert.That(writer.Calls.Count).IsEqualTo(0);
        await Assert.That(spy.FamilyDocumentExtractions).IsEqualTo(2);
    }

    [Test]
    public async Task Check_ProjectContext_CurrentMarker_ContentDrift_Stale()
    {
        // #180 is not family-doc-only: in a PROJECT the user edits type
        // values of a loaded family in place (no family editor involved).
        // The same rule must flag the drift: marker == current, but the
        // FHV10 content of the project copy differs from the version file.
        var projectDoc = SampleFiles.NewMepTemplateDocument(Application);
        if (projectDoc is null)
        {
            Skip.Test("Project templates (.rte) not found — cannot seed the project-context drift contract");
            return;
        }
        _openDocs!.Add(projectDoc);
        using (var tx = new Transaction(projectDoc, "Load child"))
        {
            tx.Start();
            if (!projectDoc.LoadFamily(_childPath!, out _))
            {
                throw new InvalidOperationException("LoadFamily(child into project) returned false");
            }
            tx.Commit();
        }

        var context = new StubRevitContext(projectDoc);
        var store = new RevitFamilyVersionStore(new RevitTransactionService(context));
        var nested = FindNestedFamily(projectDoc, ChildName);
        store.WriteToLoadedFamily(projectDoc, nested.Id,
            new FamilyVersion(1, CatalogItemId, "v1", DateTimeOffset.UtcNow, CurrentRevitMajor));

        // The local edit: TypeA's P0 value set in the project copy.
        using (var tx = new Transaction(projectDoc, "Local type edit"))
        {
            tx.Start();
            var symbol = nested.GetFamilySymbolIds()
                .Select(id => projectDoc.GetElement(id) as FamilySymbol)
                .First(s => string.Equals(s?.Name, "TypeA", StringComparison.Ordinal));
            var p0 = symbol!.LookupParameter("P0");
            if (p0 is null || !p0.Set(42.0))
            {
                throw new InvalidOperationException("Failed to set P0 on the project copy of TypeA");
            }
            tx.Commit();
        }

        var spy = new CountingSnapshotExtractor(new RevitFamilySnapshotExtractor());
        var writer = new RecordingVersionWriter(projectDoc, store);
        var detector = new StaleDetector(
            store,
            new StubCatalogProvider(new[] { CreateItem(CatalogItemId, "v1") }),
            new InlineAwaitableEvent(),
            context,
            new StubClock(),
            new NullSystemTypeFinder(),
            new NullSystemTypeVersionStore(),
            new NullFamilyTypeRepository(),
            fileResolver: new StubFileResolver(_childPath!, "v1"),
            snapshotExtractor: spy,
            contentHasher: new FamilyContentHasher(),
            versionWriter: writer);

        var results = await detector.CheckCategoryAsync(null, projectDoc, CancellationToken.None);

        var verdict = results.First(r => string.Equals(r.FamilyName, ChildName, StringComparison.OrdinalIgnoreCase));
        SmartConLogger.Info(
            $"CheckFallback project/drift: IsStale={verdict.IsStale} Reason={verdict.Reason} " +
            $"extractions={spy.FamilyDocumentExtractions}");
        await Assert.That(verdict.IsStale).IsTrue();
        await Assert.That(verdict.Reason).IsEqualTo(StaleReason.ContentDrift);
    }

    [Test]
    public async Task Check_MarkerLess_DuplicateItems_ShareSingleFileOpen()
    {
        // Performance contract: duplicate catalog items (same family name,
        // resolving to the SAME version file) pay one EditFamily each but
        // share ONE OpenDocumentFile per check run — the per-run file-hash
        // cache. Expected extractions: 2 embedded + 1 file = 3.
        var (detector, writer, _, spy) = CreateDetector(
            currentLabel: "v1",
            items: new[] { CreateItem(CatalogItemId, "v1"), CreateItem("check-item-dup", "v1") });

        var results = await detector.CheckCategoryAsync(null, _hostDoc!, CancellationToken.None);

        var verdicts = results
            .Where(r => string.Equals(r.FamilyName, ChildName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        SmartConLogger.Info(
            $"CheckFallback duplicates: rows={verdicts.Count} healed={writer.Calls.Count} " +
            $"extractions={spy.FamilyDocumentExtractions}");
        await Assert.That(verdicts.Count).IsEqualTo(2);
        await Assert.That(verdicts.All(v => !v.IsStale)).IsTrue();
        await Assert.That(writer.Calls.Count).IsEqualTo(2);
        await Assert.That(spy.FamilyDocumentExtractions).IsEqualTo(3);
    }

    [Test]
    public async Task CheckCategory_WithProgress_ReportsEveryVerifiedFamily()
    {
        // Pane progress-bar contract: one report per VERIFIED family,
        // Completed climbs 1..Total, Total = matched loadable items (+ system
        // items queued after — none in this seed), name = the family just
        // verified. Two duplicate rows → two reports (both resolve to the
        // same file and share the cached proof, but each is a checked item).
        var (detector, _, _, _) = CreateDetector(
            currentLabel: "v1",
            items: new[] { CreateItem(CatalogItemId, "v1"), CreateItem("check-item-dup", "v1") });
        var reports = new List<StaleCheckProgress>();

        var results = await detector.CheckCategoryAsync(
            null, _hostDoc!, CancellationToken.None, new SyncProgress(reports.Add));

        SmartConLogger.Info(
            $"CheckProgress: reports={reports.Count}, results={results.Count}, " +
            $"last={reports.Count - 1}");
        await Assert.That(results.Count).IsEqualTo(2);
        await Assert.That(reports.Count).IsEqualTo(2);
        await Assert.That(reports[0].Completed).IsEqualTo(1);
        await Assert.That(reports[1].Completed).IsEqualTo(2);
        await Assert.That(reports[0].Total).IsEqualTo(2);
        await Assert.That(reports[1].Total).IsEqualTo(2);
        await Assert.That(reports.All(r =>
            string.Equals(r.CurrentFamilyName, ChildName, StringComparison.OrdinalIgnoreCase))).IsTrue();
    }

    /// <summary>
    /// <see cref="Progress{T}"/> posts through the synchronization context
    /// (a race in this host) — the check must collect reports synchronously.
    /// </summary>
    private sealed class SyncProgress : IProgress<StaleCheckProgress>
    {
        private readonly Action<StaleCheckProgress> _handler;

        public SyncProgress(Action<StaleCheckProgress> handler) => _handler = handler;

        public void Report(StaleCheckProgress value) => _handler(value);
    }

    private int CurrentRevitMajor => int.Parse(Application.VersionNumber);

    private static FamilyCatalogItem CreateItem(string id, string currentLabel)
    {
        return new FamilyCatalogItem(
            Id: id,
            Name: ChildName,
            NormalizedName: ChildName.ToUpperInvariant(),
            Description: null,
            CategoryPath: null,
            CategoryId: null,
            Manufacturer: null,
            ContentStatus: ContentStatus.Active,
            CurrentVersionLabel: currentLabel,
            Tags: Array.Empty<string>(),
            PublishedBy: null,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow);
    }

    private (StaleDetector Detector, RecordingVersionWriter Writer, RevitFamilyVersionStore Store, CountingSnapshotExtractor Spy)
        CreateDetector(string currentLabel, IReadOnlyList<FamilyCatalogItem>? items = null)
    {
        var context = new StubRevitContext(_hostDoc!);
        var store = new RevitFamilyVersionStore(new RevitTransactionService(context));
        var spy = new CountingSnapshotExtractor(new RevitFamilySnapshotExtractor());
        var writer = new RecordingVersionWriter(_hostDoc, store);
        var detector = new StaleDetector(
            store,
            new StubCatalogProvider(items ?? new[] { CreateItem(CatalogItemId, currentLabel) }),
            new InlineAwaitableEvent(),
            context,
            new StubClock(),
            new NullSystemTypeFinder(),
            new NullSystemTypeVersionStore(),
            new NullFamilyTypeRepository(),
            fileResolver: new StubFileResolver(_childPath!, currentLabel),
            snapshotExtractor: spy,
            contentHasher: new FamilyContentHasher(),
            versionWriter: writer);
        return (detector, writer, store, spy);
    }

    private void BumpChildToV2()
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

    /// <summary>
    /// v2 that differs from v1 ONLY by P0's parameter group
    /// (General → Data): FHV10 ignores the change (groups are not content).
    /// In-place regroup where the API allows it (R24+, same parameter
    /// identity — the faithful UI-regroup simulation), remove+add on older
    /// versions.
    /// </summary>
    private void BumpChildToGroupOnlyV2()
    {
        var doc = Application.OpenDocumentFile(_childPath!);
        try
        {
#if REVIT2024_OR_GREATER
            using (var tx = new Transaction(doc, "v2 group-only"))
            {
                tx.Start();
                var p0 = doc.FamilyManager.GetParameters()
                    .First(x => string.Equals(x.Definition?.Name, "P0", StringComparison.Ordinal));
                ((InternalDefinition)p0.Definition).SetGroupTypeId(GroupTypeId.Data);
                tx.Commit();
            }
#else
            using (var tx = new Transaction(doc, "v2 remove P0"))
            {
                tx.Start();
                var p0 = doc.FamilyManager.GetParameters()
                    .First(x => string.Equals(x.Definition?.Name, "P0", StringComparison.Ordinal));
                doc.FamilyManager.RemoveParameter(p0);
                tx.Commit();
            }
            using (var tx = new Transaction(doc, "v2 re-add P0 in Data"))
            {
                tx.Start();
#if REVIT2022_OR_GREATER
                doc.FamilyManager.AddParameter("P0", GroupTypeId.Data, SpecTypeId.Number, false);
#else
                doc.FamilyManager.AddParameter("P0", BuiltInParameterGroup.PG_DATA, ParameterType.Number, false);
#endif
                tx.Commit();
            }
#endif
            doc.Save();
        }
        finally
        {
            doc.Close(false);
        }
    }

    private static Family FindNestedFamily(Document doc, string familyName)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(Family))
            .Cast<Family>()
            .First(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Simulates a local edit-in-place of the embedded child: adds a scratch
    /// parameter to the EditFamily copy and loads it back into the host
    /// (doc-to-doc manages its own transaction — the host must not be
    /// modifiable). The ES marker on the host's Family element is untouched,
    /// so the check sees "marker current, content drifted".
    /// </summary>
    private void EditEmbeddedChildLocally()
    {
        var nested = FindNestedFamily(_hostDoc!, ChildName);
        var copy = _hostDoc!.EditFamily(nested);
        try
        {
            using (var tx = new Transaction(copy, "Local edit"))
            {
                tx.Start();
#if REVIT2022_OR_GREATER
                copy.FamilyManager.AddParameter("P9", GroupTypeId.General, SpecTypeId.Number, false);
#else
                copy.FamilyManager.AddParameter("P9", BuiltInParameterGroup.PG_GENERAL, ParameterType.Number, false);
#endif
                tx.Commit();
            }
            var pushed = copy.LoadFamily(_hostDoc!, new OverwriteLoadOptions());
            if (pushed is null)
            {
                throw new InvalidOperationException("LoadFamily(edited copy back into host) returned null");
            }
        }
        finally
        {
            copy.Close(false);
        }
    }

    private sealed class OverwriteLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = true;
            return true;
        }

        public bool OnSharedFamilyFound(
            Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = true;
            return true;
        }
    }
}
