using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.Stale;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// Contracts for the 2026-08-13 follow-up fixes (manual-test findings):
/// <list type="number">
/// <item>#218 — an ORPHANED ES marker (its CatalogItemId no longer exists in
/// the catalog, e.g. the item was deleted and re-imported under a new id) is
/// re-resolved by content: match → not stale + the marker is healed with the
/// resolved id; a FOREIGN id of another LIVE item stays a hard stale
/// (corrupted ES) with no heal.</item>
/// <item>#220 — <see cref="StaleDetector.GetMergedSnapshot"/> on a cold /
/// invalidated cache starts from the empty snapshot (never a silent no-op),
/// so the post-DnD tree rebuild always recomputes badges.</item>
/// <item>#222 — the loadable update path verifies content uniformly in
/// PROJECT documents (pre-verify skip, failed-reload arbitration,
/// post-verify), and the single-update result carries the per-type change
/// report (changed types split by loaded / not-loaded in the project).</item>
/// </list>
/// </summary>
public sealed class StaleFollowupContractTests : RevitApiTest
{
    private const string ChildName = "SmartConFollowupChild";
    private const string Child2Name = "SmartConFollowupChild2";
    private const string CatalogItemId = "followup-item";
    private const string CatalogItemId2 = "followup-item-2";

    private string? _tempDir;
    private string? _template;
    private string? _childPath;
    private string? _child2Path;
    private Document? _hostDoc;
    private List<Document>? _openDocs;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void Seed()
    {
        _template = SampleFiles.FindFamilyTemplate(Application);
        if (_template is null)
        {
            Skip.Test("Family templates (.rft) not found — cannot seed the follow-up contracts");
            return;
        }

        _tempDir = Path.Combine(Path.GetTempPath(), $"SmartConFollowup_{Guid.NewGuid().ToString("N")}");
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
            // TypeB: the partial-load contracts (preserve-types reload keeps
            // the project's SUBSET — the verify must compare the type
            // intersection, not the whole TYPES section).
            doc.FamilyManager.NewType("TypeB");
            tx.Commit();
        }
        doc.SaveAs(_childPath!, new SaveAsOptions { OverwriteExistingFile = true });
        doc.Close(false);

        // Child2: TypeA ONLY — the family-doc type-set-change contract
        // (v2 adds TypeB; the update must not be pre-verify-skipped).
        _child2Path = Path.Combine(_tempDir, Child2Name + ".rfa");
        var doc2 = Application.NewFamilyDocument(_template);
        using (var tx = new Transaction(doc2, "v1"))
        {
            tx.Start();
            doc2.OwnerFamily?.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.Set(1);
#if REVIT2022_OR_GREATER
            doc2.FamilyManager.AddParameter("P0", GroupTypeId.General, SpecTypeId.Number, false);
#else
            doc2.FamilyManager.AddParameter("P0", BuiltInParameterGroup.PG_GENERAL, ParameterType.Number, false);
#endif
            doc2.FamilyManager.NewType("TypeA");
            tx.Commit();
        }
        doc2.SaveAs(_child2Path!, new SaveAsOptions { OverwriteExistingFile = true });
        doc2.Close(false);

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

    // ── #220 ────────────────────────────────────────────────────────────

    [Test]
    public async Task MergedSnapshot_ColdCache_StartsFromEmpty_NeverNull()
    {
        // #220 regression: pre-fix GetMergedSnapshot returned null on a cold
        // cache and the VM logged "merged snapshot is null — badges not
        // updated" after a DnD drop; the badges froze until the next manual
        // Check. The merge now starts from the empty snapshot.
        var (detector, _, _, _) = CreateDetector(currentLabel: "v1");

        var empty = detector.GetMergedSnapshot([]);
        await Assert.That(empty).IsNotNull();
        await Assert.That(empty.Results.Count).IsEqualTo(0);

        var applied = detector.GetMergedSnapshot([
            new StaleCheckResult(CatalogItemId, ChildName, "v2", "v1", true, StaleReason.VersionMismatch)]);
        await Assert.That(applied.Results.Count).IsEqualTo(1);
        await Assert.That(applied.Results[CatalogItemId].IsStale).IsTrue();

        // The merge is a read-fold — the cache itself is written only by
        // Check*/MarkUpdated paths.
        await Assert.That(detector.GetCachedSnapshot()).IsNull();
    }

    // ── #218 ────────────────────────────────────────────────────────────

    [Test]
    public async Task Check_OrphanedMarker_ContentMatches_NotStale_AndHealsWithResolvedId()
    {
        // #218 core scenario: the marker references an id that no longer
        // exists in the catalog (the item was deleted and re-imported under
        // a new id). Instead of the misleading "ES data is corrupted" WARN
        // the check re-resolves by content: the copy matches the current
        // version → not stale, and the marker is HEALED with the resolved id.
        var (detector, writer, store, spy) = CreateDetector(currentLabel: "v1");
        var nested = FindNestedFamily(_hostDoc!, ChildName);
        store.WriteToLoadedFamily(_hostDoc!, nested.Id,
            new FamilyVersion(1, "ghost-removed-item", "v1", DateTimeOffset.UtcNow, CurrentRevitMajor));

        var results = await detector.CheckCategoryAsync(null, _hostDoc!, CancellationToken.None);

        var verdict = results.First(r => string.Equals(r.FamilyName, ChildName, StringComparison.OrdinalIgnoreCase));
        SmartConLogger.Info(
            $"Followup orphan-heal: IsStale={verdict.IsStale} Reason={verdict.Reason} " +
            $"healed={writer.Calls.Count} extractions={spy.FamilyDocumentExtractions}");
        await Assert.That(verdict.IsStale).IsFalse();
        await Assert.That(writer.Calls.Count).IsEqualTo(1);
        await Assert.That(writer.Calls[0].CatalogItemId).IsEqualTo(CatalogItemId);
        await Assert.That(writer.Calls[0].VersionLabel).IsEqualTo("v1");

        var healed = store.ReadFromLoadedFamily(_hostDoc!, nested.Id);
        await Assert.That(healed).IsNotNull();
        await Assert.That(healed!.CatalogItemId).IsEqualTo(CatalogItemId);
    }

    [Test]
    public async Task Check_OrphanedMarker_ContentDiffers_StaysStale_NoHeal()
    {
        // Orphan + genuinely older content: the marker cannot be healed to
        // the current version (that would lie about the version) — the item
        // is stale (ContentDrift) and «Обновить» is the reconciliation path.
        BumpChildToV2();
        var (detector, writer, store, _) = CreateDetector(currentLabel: "v2");
        var nested = FindNestedFamily(_hostDoc!, ChildName);
        store.WriteToLoadedFamily(_hostDoc!, nested.Id,
            new FamilyVersion(1, "ghost-removed-item", "v1", DateTimeOffset.UtcNow, CurrentRevitMajor));

        var results = await detector.CheckCategoryAsync(null, _hostDoc!, CancellationToken.None);

        var verdict = results.First(r => string.Equals(r.FamilyName, ChildName, StringComparison.OrdinalIgnoreCase));
        SmartConLogger.Info(
            $"Followup orphan-drift: IsStale={verdict.IsStale} Reason={verdict.Reason} healed={writer.Calls.Count}");
        await Assert.That(verdict.IsStale).IsTrue();
        await Assert.That(verdict.Reason).IsEqualTo(StaleReason.ContentDrift);
        await Assert.That(writer.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Check_ForeignLiveMarkerId_StaysStale_NoHeal_NoVerify()
    {
        // The OTHER #218 branch: the marker id EXISTS but belongs to another
        // live catalog item — genuinely corrupted ES data. Hard stale, no
        // heal, and no content verification for the mismatched row (the
        // marker CAN speak — it says "corrupted"). The sibling item whose id
        // matches still verifies clean (2 extractions: embedded + file).
        var items = new[] { CreateItem(CatalogItemId, "v1"), CreateItem("other-item", "v1") };
        var (detector, writer, store, spy) = CreateDetector(currentLabel: "v1", items: items);
        var nested = FindNestedFamily(_hostDoc!, ChildName);
        store.WriteToLoadedFamily(_hostDoc!, nested.Id,
            new FamilyVersion(1, "other-item", "v1", DateTimeOffset.UtcNow, CurrentRevitMajor));

        var results = await detector.CheckCategoryAsync(null, _hostDoc!, CancellationToken.None);

        var corrupted = results.First(r => string.Equals(r.CatalogItemId, CatalogItemId, StringComparison.Ordinal));
        var matching = results.First(r => string.Equals(r.CatalogItemId, "other-item", StringComparison.Ordinal));
        SmartConLogger.Info(
            $"Followup foreign-id: corrupted={corrupted.IsStale}/{corrupted.Reason} " +
            $"matching={matching.IsStale} healed={writer.Calls.Count} extractions={spy.FamilyDocumentExtractions}");
        await Assert.That(corrupted.IsStale).IsTrue();
        await Assert.That(corrupted.Reason).IsEqualTo(StaleReason.VersionMismatch);
        await Assert.That(matching.IsStale).IsFalse();
        await Assert.That(writer.Calls.Count).IsEqualTo(0);
        await Assert.That(spy.FamilyDocumentExtractions).IsEqualTo(2);
    }

    // ── #222 ────────────────────────────────────────────────────────────

    [Test]
    public async Task Update_ProjectContext_PreVerifySkip_ContentAlreadyCurrent()
    {
        // #222: in a PROJECT the pre-verify now applies too — the embedded
        // content already equals the resolved file, so the update is a no-op
        // success WITHOUT any reload (marker only), reported as
        // ContentAlreadyCurrent ("уже актуально", not "обновлено").
        var projectDoc = SeedProjectWithChild();
        var writer = new RecordingVersionWriter(projectDoc,
            new RevitFamilyVersionStore(new RevitTransactionService(new StubRevitContext(projectDoc))));
        var updater = CreateProjectUpdater(projectDoc, writer, _childPath!, "v1");

        var result = await updater.UpdateFamilyAsync(
            CatalogItemId, overwriteParameterValues: true, fromVersionLabel: null, CancellationToken.None);

        SmartConLogger.Info(
            $"Followup project pre-verify skip: Success={result.Success} AlreadyCurrent={result.ContentAlreadyCurrent} " +
            $"markers={writer.Calls.Count}");
        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.ContentAlreadyCurrent).IsTrue();
        await Assert.That(writer.Calls.Count).IsEqualTo(1);
        await Assert.That(writer.Calls[0].VersionLabel).IsEqualTo("v1");
    }

    [Test]
    public async Task Update_ProjectContext_PostVerify_VerifiedReload_MarkerWritten()
    {
        // #222: the project reload (v1 → v2) is now post-verified against the
        // resolved file hash — previously the verify returned "not
        // applicable" in a project and the marker was written on blind
        // trust. A verified success writes the marker and is NOT reported
        // as already-current.
        var projectDoc = SeedProjectWithChild();
        BumpChildToV2();
        var store = new RevitFamilyVersionStore(new RevitTransactionService(new StubRevitContext(projectDoc)));
        var writer = new RecordingVersionWriter(projectDoc, store);
        var updater = CreateProjectUpdater(projectDoc, writer, _childPath!, "v2");

        var result = await updater.UpdateFamilyAsync(
            CatalogItemId, overwriteParameterValues: true, fromVersionLabel: null, CancellationToken.None);

        SmartConLogger.Info(
            $"Followup project post-verify positive: Success={result.Success} AlreadyCurrent={result.ContentAlreadyCurrent} " +
            $"markers={writer.Calls.Count}");
        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.ContentAlreadyCurrent).IsFalse();
        await Assert.That(writer.Calls.Count).IsEqualTo(1);
        await Assert.That(writer.Calls[0].VersionLabel).IsEqualTo("v2");

        // The reload really landed: the project copy now carries P2.
        var nested = FindNestedFamily(projectDoc, ChildName);
        var copy = projectDoc.EditFamily(nested);
        try
        {
            await Assert.That(
                copy.FamilyManager.GetParameters().Any(p => string.Equals(p.Definition?.Name, "P2", StringComparison.Ordinal)))
                .IsTrue();
        }
        finally
        {
            copy.Close(false);
        }
    }

    [Test]
    public async Task Update_ProjectContext_PostVerifyMismatch_FailsWithoutMarker()
    {
        // #222 negative contract in a PROJECT (mirror of the family-document
        // orchestration negative): the post-verify mismatch fails the update
        // and NO marker is written — the family stays honestly stale instead
        // of wearing a marker that lies about the version.
        var projectDoc = SeedProjectWithChild();
        BumpChildToV2();
        var writer = new RecordingVersionWriter(projectDoc,
            new RevitFamilyVersionStore(new RevitTransactionService(new StubRevitContext(projectDoc))));
        var updater = CreateProjectUpdater(projectDoc, writer, _childPath!, "v2", new CorruptEmbeddedVerifyHasher());

        var result = await updater.UpdateFamilyAsync(
            CatalogItemId, overwriteParameterValues: true, fromVersionLabel: null, CancellationToken.None);

        SmartConLogger.Info(
            $"Followup project post-verify negative: Success={result.Success} markers={writer.Calls.Count}");
        await Assert.That(result.Success).IsFalse();
        await Assert.That(writer.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Update_ProjectContext_PostVerify_PartialTypeSet_VerifiedReload()
    {
        // Stress test 2026-08-14 (owner's false «Ошибка загрузки»): the
        // project has ONLY TypeA of a two-type family loaded (preserve-types
        // reload never pulls the rest, #101). The unrestricted FHV10
        // comparison could never match (TYPES 1≠2) and failed the update
        // AFTER the reload really landed. With the type-set rule the
        // post-verify compares the intersection and the update succeeds.
        var projectDoc = SeedProjectWithChildTypeAOnly();
        BumpChildToV2();
        var store = new RevitFamilyVersionStore(new RevitTransactionService(new StubRevitContext(projectDoc)));
        var writer = new RecordingVersionWriter(projectDoc, store);
        var updater = CreateProjectUpdater(projectDoc, writer, _childPath!, "v2");

        var result = await updater.UpdateFamilyAsync(
            CatalogItemId, overwriteParameterValues: true, fromVersionLabel: null, CancellationToken.None);

        SmartConLogger.Info(
            $"Followup partial-type update: Success={result.Success} AlreadyCurrent={result.ContentAlreadyCurrent} " +
            $"markers={writer.Calls.Count}");
        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.ContentAlreadyCurrent).IsFalse();
        await Assert.That(writer.Calls.Count).IsEqualTo(1);
        await Assert.That(writer.Calls[0].VersionLabel).IsEqualTo("v2");
    }

    [Test]
    public async Task Check_ProjectContext_CurrentMarker_PartialTypeSet_NotStale()
    {
        // Same type-set rule on the CHECK side: marker == current, the
        // project has only TypeA loaded — content re-verify must compare
        // the intersection and stay "not stale" (the unrestricted
        // comparison reported a false ContentDrift).
        var projectDoc = SeedProjectWithChildTypeAOnly();
        var context = new StubRevitContext(projectDoc);
        var store = new RevitFamilyVersionStore(new RevitTransactionService(context));
        var nested = FindNestedFamily(projectDoc, ChildName);
        store.WriteToLoadedFamily(projectDoc, nested.Id,
            new FamilyVersion(1, CatalogItemId, "v1", DateTimeOffset.UtcNow, CurrentRevitMajor));

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
            $"Followup partial-type check: IsStale={verdict.IsStale} Reason={verdict.Reason} " +
            $"extractions={spy.FamilyDocumentExtractions}");
        await Assert.That(verdict.IsStale).IsFalse();
        await Assert.That(writer.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Update_TypeChangeReport_SplitsLoadedVsNotLoaded()
    {
        // #222 UX report: the diff between the previously loaded version
        // (fromVersionLabel) and the target is computed from the extracted
        // attribute rows and split by the set of types loaded in the
        // project. «Успешно» can then say WHICH types actually changed —
        // and never reads as «мой параметр обновился» when the change
        // landed in a type the project does not have loaded.
        var projectDoc = SeedProjectWithChild();
        BumpChildToV2();
        var writer = new RecordingVersionWriter(projectDoc,
            new RevitFamilyVersionStore(new RevitTransactionService(new StubRevitContext(projectDoc))));

        var fromVersion = new FamilyCatalogVersion(
            Id: "from-version-id", CatalogItemId: CatalogItemId, FileId: "f1",
            VersionLabel: "v1", RevitMajorVersion: CurrentRevitMajor,
            TypesCount: 1, ParametersCount: 1, PublishedAtUtc: DateTimeOffset.UtcNow);
        var catalog = new StubVersionedCatalogProvider(
            CreateItem(CatalogItemId, "v2"),
            new Dictionary<string, FamilyCatalogVersion>(StringComparer.Ordinal) { ["v1"] = fromVersion });

        // StubFileResolver resolves the TARGET with VersionId "version-id"
        // (see StaleUpdateTestFakes) — the stub repos key rows by it.
        var values = new StubAttributeValueRepository(new Dictionary<string, IReadOnlyList<ExtractedAttributeValue>>(StringComparer.Ordinal)
        {
            ["from-version-id"] = new[] { AttrValue("t1", "P0", "1") },
            ["version-id"] = new[] { AttrValue("t1", "P0", "2"), AttrValue("t2", "P0", "3") },
        });
        var types = new StubVersionedTypeRepository(new Dictionary<string, IReadOnlyList<FamilyTypeDescriptor>>(StringComparer.Ordinal)
        {
            ["from-version-id"] = new[] { TypeDescriptor("t1", "TypeA") },
            ["version-id"] = new[] { TypeDescriptor("t1", "TypeA"), TypeDescriptor("t2", "TypeB") },
        });
        var search = new StubFamilySearchService(new[] { "TypeA" });

        var updater = CreateProjectUpdater(
            projectDoc, writer, _childPath!, "v2",
            hasher: null, catalog: catalog, attributeValues: values, typeRepository: types, familySearch: search);

        var result = await updater.UpdateFamilyAsync(
            CatalogItemId, overwriteParameterValues: true, fromVersionLabel: "v1", CancellationToken.None);

        SmartConLogger.Info(
            $"Followup type-change report: Success={result.Success} Available={result.TypeDiffAvailable} " +
            $"loaded=[{string.Join(",", result.ChangedLoadedTypeNames)}] notLoaded=[{string.Join(",", result.ChangedNotLoadedTypeNames)}]");
        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.TypeDiffAvailable).IsTrue();
        await Assert.That(result.ChangedLoadedTypeNames).IsEquivalentTo(new[] { "TypeA" });
        await Assert.That(result.ChangedNotLoadedTypeNames).IsEquivalentTo(new[] { "TypeB" });
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private int CurrentRevitMajor => int.Parse(Application.VersionNumber);

    private Document SeedProjectWithChild()
    {
        var projectDoc = SampleFiles.NewMepTemplateDocument(Application);
        if (projectDoc is null)
        {
            Skip.Test("Project templates (.rte) not found — cannot seed the project-context contract");
            throw new InvalidOperationException("unreachable — Skip.Test throws");
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
        return projectDoc;
    }

    /// <summary>
    /// Project with ONLY TypeA of the two-type child loaded — the partial
    /// type set the preserve-types reload maintains by design (#101).
    /// </summary>
    private Document SeedProjectWithChildTypeAOnly()
    {
        var projectDoc = SampleFiles.NewMepTemplateDocument(Application);
        if (projectDoc is null)
        {
            Skip.Test("Project templates (.rte) not found — cannot seed the project-context contract");
            throw new InvalidOperationException("unreachable — Skip.Test throws");
        }
        _openDocs!.Add(projectDoc);
        using (var tx = new Transaction(projectDoc, "Load TypeA only"))
        {
            tx.Start();
            if (!projectDoc.LoadFamilySymbol(_childPath!, "TypeA", out _))
            {
                throw new InvalidOperationException("LoadFamilySymbol(TypeA into project) returned false");
            }
            tx.Commit();
        }
        return projectDoc;
    }

    private StaleFamilyUpdater CreateProjectUpdater(
        Document projectDoc,
        RecordingVersionWriter writer,
        string resolvedPath,
        string resolvedLabel,
        IFamilyContentHasher? hasher = null,
        IFamilyCatalogProvider? catalog = null,
        IAttributeValueRepository? attributeValues = null,
        IFamilyTypeRepository? typeRepository = null,
        IFamilySearchService? familySearch = null)
    {
        var context = new StubRevitContext(projectDoc);
        var loadService = new RevitFamilyLoadService(context, new RevitTransactionService(context));
        return new StaleFamilyUpdater(
            loadService,
            new StubFileResolver(resolvedPath, resolvedLabel),
            new RevitFamilyVersionStore(new RevitTransactionService(context)),
            new NullFamilyManagerDialogService(),
            new InlineAwaitableEvent(),
            context,
            writer,
            new StubClock(),
            nestedSharedRepository: null,
            catalog: catalog,
            typeRepository: typeRepository,
            systemSyncOrchestrator: null,
            dependencyRepository: null,
            snapshotExtractor: new RevitFamilySnapshotExtractor(),
            contentHasher: hasher ?? new FamilyContentHasher(),
            attributeValueRepository: attributeValues,
            familySearchService: familySearch);
    }

    private static ExtractedAttributeValue AttrValue(string typeId, string param, string text) =>
        new(
            Id: Guid.NewGuid().ToString("N"),
            CatalogItemId: CatalogItemId,
            VersionId: null,
            FileId: null,
            TypeId: typeId,
            AttributeId: null,
            BindingId: null,
            ParameterName: param,
            ParameterScope: AttributeScope.Type,
            StorageType: "String",
            ValueText: text,
            ValueRaw: null,
            ValueNumber: null,
            UnitTypeId: null,
            Status: AttributeValueStatus.Found,
            Message: null,
            ExtractionRunId: "run",
            ExtractedAtUtc: new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private static FamilyTypeDescriptor TypeDescriptor(string id, string name) =>
        new(Id: id, CatalogItemId: CatalogItemId, Name: name, SortOrder: 0);

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

    [Test]
    public async Task Update_FamilyDoc_TypeSetChange_ReloadNotSkipped_TypeBLands()
    {
        // Validator finding 2026-08-14: in a FAMILY document the merge
        // transfers the WHOLE type set, so a version bump that only ADDS a
        // type (TypeA untouched) must mismatch pre-verify (full-vs-full) —
        // a project-style intersection restriction would falsely skip the
        // reload and the new type would never land while the marker
        // claimed v2.
        var host2 = Application.NewFamilyDocument(_template!);
        _openDocs!.Add(host2);
        using (var tx = new Transaction(host2, "Load child2"))
        {
            tx.Start();
            if (!host2.LoadFamily(_child2Path!, out _))
            {
                throw new InvalidOperationException("LoadFamily(child2) returned false");
            }
            tx.Commit();
        }
        BumpChild2AddsTypeB();

        var context = new StubRevitContext(host2);
        var counting = new CountingLoadService(
            new RevitFamilyLoadService(context, new RevitTransactionService(context)));
        var store = new RevitFamilyVersionStore(new RevitTransactionService(context));
        var writer = new RecordingVersionWriter(host2, store);
        var updater = new StaleFamilyUpdater(
            counting,
            new StubFileResolver(_child2Path!, "v2"),
            store,
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
            contentHasher: new FamilyContentHasher());

        var result = await updater.UpdateFamilyAsync(
            CatalogItemId2, overwriteParameterValues: true, fromVersionLabel: null, CancellationToken.None);

        SmartConLogger.Info(
            $"Followup family-doc typeset-change: Success={result.Success} " +
            $"reloads={counting.NestedReloadCalls} markers={writer.Calls.Count}");
        await Assert.That(result.Success).IsTrue();
        await Assert.That(counting.NestedReloadCalls).IsEqualTo(1);
        await Assert.That(writer.Calls.Count).IsEqualTo(1);
        await Assert.That(writer.Calls[0].VersionLabel).IsEqualTo("v2");

        var child2 = FindNestedFamily(host2, Child2Name);
        var typeBLanded = child2.GetFamilySymbolIds()
            .Select(id => host2.GetElement(id) as FamilySymbol)
            .Any(s => string.Equals(s?.Name, "TypeB", StringComparison.Ordinal));
        await Assert.That(typeBLanded).IsTrue();
    }

    private void BumpChild2AddsTypeB()
    {
        var doc = Application.OpenDocumentFile(_child2Path!);
        try
        {
            using (var tx = new Transaction(doc, "v2 add TypeB"))
            {
                tx.Start();
                doc.FamilyManager.NewType("TypeB");
                tx.Commit();
            }
            doc.Save();
        }
        finally
        {
            doc.Close(false);
        }
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

    private static Family FindNestedFamily(Document doc, string familyName)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(Family))
            .Cast<Family>()
            .First(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase));
    }

    // ── #222 report stubs ───────────────────────────────────────────────

    private sealed class StubVersionedCatalogProvider : IFamilyCatalogProvider
    {
        private readonly FamilyCatalogItem _item;
        private readonly IReadOnlyDictionary<string, FamilyCatalogVersion> _versionsByLabel;

        public StubVersionedCatalogProvider(
            FamilyCatalogItem item,
            IReadOnlyDictionary<string, FamilyCatalogVersion> versionsByLabel)
        {
            _item = item;
            _versionsByLabel = versionsByLabel;
        }

        public FamilyCatalogCapabilities GetCapabilities()
            => new(true, true, true, true, true, CatalogProviderKind.Local);
        public Task<IReadOnlyList<FamilyCatalogItem>> SearchAsync(FamilyCatalogQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FamilyCatalogItem>>(new[] { _item });
        public Task<FamilyCatalogItem?> GetItemAsync(string id, CancellationToken ct = default)
            => Task.FromResult<FamilyCatalogItem?>(string.Equals(id, _item.Id, StringComparison.Ordinal) ? _item : null);
        public Task<IReadOnlyList<FamilyCatalogVersion>> GetVersionsAsync(string catalogItemId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FamilyCatalogVersion>>(_versionsByLabel.Values.ToList());
        public Task<FamilyCatalogVersion?> GetVersionByIdAsync(string catalogItemId, string versionId, CancellationToken ct = default)
            => Task.FromResult<FamilyCatalogVersion?>(_versionsByLabel.Values.FirstOrDefault(v => string.Equals(v.Id, versionId, StringComparison.Ordinal)));
        public Task<FamilyCatalogVersion?> GetVersionByLabelAsync(string catalogItemId, string versionLabel, int targetRevitMajorVersion = 0, CancellationToken ct = default)
            => Task.FromResult<FamilyCatalogVersion?>(_versionsByLabel.TryGetValue(versionLabel, out var v) ? v : null);
        public Task<FamilyFileRecord?> GetFileAsync(string fileId, CancellationToken ct = default)
            => Task.FromResult<FamilyFileRecord?>(null);
        public Task<int> GetItemCountAsync(CancellationToken ct = default) => Task.FromResult(1);
        public Task<IReadOnlyList<int>> GetAvailableRevitVersionsAsync(string catalogItemId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());
        public Task<FamilyCatalogItem?> FindByNormalizedNameAsync(string normalizedName, string? familySource = null, CancellationToken ct = default)
            => Task.FromResult<FamilyCatalogItem?>(null);
        public Task<FamilyCatalogItem?> FindByRevitCategoryIdAsync(int revitCategoryId, string familySource, CancellationToken ct = default)
            => Task.FromResult<FamilyCatalogItem?>(null);
        public Task<ContentHashMatch?> FindByContentHashAcrossVersionsAsync(string hexHash, int hashFormatVersion, string familySource, CancellationToken ct = default)
            => Task.FromResult<ContentHashMatch?>(null);
        public Task<IReadOnlyList<FamilyCatalogItem>> GetItemsBySourceAsync(string familySource, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FamilyCatalogItem>>(new[] { _item });
        public Task<IReadOnlyList<string>> GetAllTagsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    private sealed class StubAttributeValueRepository : IAttributeValueRepository
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<ExtractedAttributeValue>> _byVersionId;

        public StubAttributeValueRepository(IReadOnlyDictionary<string, IReadOnlyList<ExtractedAttributeValue>> byVersionId)
        {
            _byVersionId = byVersionId;
        }

        public Task<IReadOnlyList<ExtractedAttributeValue>> GetValuesForItemAsync(string catalogItemId, string? versionId, CancellationToken ct = default)
            => Task.FromResult(versionId is not null && _byVersionId.TryGetValue(versionId, out var v)
                ? v
                : Array.Empty<ExtractedAttributeValue>());
        public Task<IReadOnlyList<ExtractedAttributeValue>> GetValuesForTypeAsync(string typeId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ExtractedAttributeValue>>(Array.Empty<ExtractedAttributeValue>());
        public Task<IReadOnlyList<ExtractedAttributeValue>> GetValuesForRunAsync(string runId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ExtractedAttributeValue>>(Array.Empty<ExtractedAttributeValue>());
        public Task SaveValuesAsync(IReadOnlyList<ExtractedAttributeValue> values, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task ReplaceSnapshotAsync(string catalogItemId, string? versionId, string runId, IReadOnlyList<ExtractedAttributeValue> values, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<int> DeleteValuesForRunAsync(string runId, CancellationToken ct = default)
            => Task.FromResult(0);
        public Task<int> GetFoundCountAsync(string catalogItemId, string? versionId, CancellationToken ct = default)
            => Task.FromResult(0);
        public Task<int> GetMissingCountAsync(string catalogItemId, string? versionId, CancellationToken ct = default)
            => Task.FromResult(0);
    }

    private sealed class StubVersionedTypeRepository : IFamilyTypeRepository
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyList<FamilyTypeDescriptor>> _byVersionId;

        public StubVersionedTypeRepository(IReadOnlyDictionary<string, IReadOnlyList<FamilyTypeDescriptor>> byVersionId)
        {
            _byVersionId = byVersionId;
        }

        public Task<IReadOnlyList<FamilyTypeDescriptor>> GetTypesForItemAsync(string catalogItemId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FamilyTypeDescriptor>>(Array.Empty<FamilyTypeDescriptor>());
        public Task<IReadOnlyList<FamilyTypeDescriptor>> GetTypesForItemVersionAsync(string catalogItemId, string? versionId, CancellationToken ct = default)
            => Task.FromResult(versionId is not null && _byVersionId.TryGetValue(versionId, out var v)
                ? v
                : Array.Empty<FamilyTypeDescriptor>());
        public Task<IReadOnlyDictionary<string, IReadOnlyList<FamilyTypeDescriptor>>> GetAllTypesBatchAsync(IEnumerable<string> catalogItemIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<FamilyTypeDescriptor>>>(
                new Dictionary<string, IReadOnlyList<FamilyTypeDescriptor>>());
        public Task<IReadOnlyDictionary<string, string>> SyncTypesAsync(
            string catalogItemId, string? versionId, string? fileId, string runId,
            IReadOnlyList<FamilyTypeDescriptor> types, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        public Task<bool> HasTypesAsync(string catalogItemId, CancellationToken ct = default)
            => Task.FromResult(false);
    }

    private sealed class StubFamilySearchService : IFamilySearchService
    {
        private readonly IReadOnlyList<string> _typeNames;

        public StubFamilySearchService(IReadOnlyList<string> typeNames)
        {
            _typeNames = typeNames;
        }

        public bool IsFamilyLoaded(string familyName) => true;
        public IReadOnlyList<string> GetFamilyTypeNames(string familyName) => _typeNames;
        public bool HasFamilyType(string familyName, string typeName)
            => _typeNames.Contains(typeName, StringComparer.OrdinalIgnoreCase);
    }
}
