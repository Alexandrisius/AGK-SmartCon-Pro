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
/// Issue #249 (Phase 2): per-type stale verification for LOADABLE
/// families. Before #249 the verdict was family-scoped — one locally
/// edited type of 50 marked every loaded type stale. The content
/// verification now produces a per-type drift map
/// (<see cref="IStaleDetector.GetLoadableTypeStaleMap"/>): the orange
/// dot belongs to the exact drifted types, and a family-level match
/// clears the map (the tree falls back to the leaf verdict).
/// </summary>
public sealed class StalePerTypeVerificationTests : RevitApiTest
{
    private const string ChildName = "SmartConPerTypeChild";
    private const string CatalogItemId = "per-type-item";

    private string? _tempDir;
    private string? _template;
    private string? _childPath;
    private List<Document>? _openDocs;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void Seed()
    {
        _template = SampleFiles.FindFamilyTemplate(Application);
        if (_template is null)
        {
            Skip.Test("Family templates (.rft) not found — cannot seed the per-type contracts");
            return;
        }

        _tempDir = Path.Combine(Path.GetTempPath(), $"SmartConPerType_{Guid.NewGuid().ToString("N")}");
        Directory.CreateDirectory(_tempDir);
        _childPath = Path.Combine(_tempDir, ChildName + ".rfa");
        _openDocs = new List<Document>();

        // Two-type child with per-type P0 values: TypeA=1, TypeB=2.
        var doc = Application.NewFamilyDocument(_template);
        using (var tx = new Transaction(doc, "v1"))
        {
            tx.Start();
            doc.OwnerFamily?.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.Set(1);
#if REVIT2022_OR_GREATER
            var p0 = doc.FamilyManager.AddParameter("P0", GroupTypeId.General, SpecTypeId.Number, false);
#else
            var p0 = doc.FamilyManager.AddParameter("P0", BuiltInParameterGroup.PG_GENERAL, ParameterType.Number, false);
#endif
            var typeA = doc.FamilyManager.NewType("TypeA");
            doc.FamilyManager.CurrentType = typeA;
            doc.FamilyManager.Set(p0, 1.0);
            var typeB = doc.FamilyManager.NewType("TypeB");
            doc.FamilyManager.CurrentType = typeB;
            doc.FamilyManager.Set(p0, 2.0);
            tx.Commit();
        }
        doc.SaveAs(_childPath!, new SaveAsOptions { OverwriteExistingFile = true });
        doc.Close(false);
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
                        // best-effort cleanup
                    }
                }
            }
        }
        finally
        {
            try
            {
                if (_tempDir is not null && Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, recursive: true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    [Test]
    public async Task Check_OneTypeEditedLocally_PerTypeMapFlagsOnlyThatType()
    {
        // Marker == current v1; the local edit touches ONLY TypeA's P0
        // value (1 → 99). The family verdict is ContentDrift, and the
        // per-type map must flag TypeA only — TypeB stays clean.
        var projectDoc = SeedProjectWithChild();
        var detector = CreateDetector(projectDoc, out var store);
        WriteCurrentMarker(projectDoc, store);

        EditEmbeddedChildTypeValueLocally(projectDoc, "TypeA", 99.0);

        var results = await detector.CheckCategoryAsync(null, projectDoc, CancellationToken.None);

        var verdict = results.First(r => string.Equals(r.FamilyName, ChildName, StringComparison.OrdinalIgnoreCase));
        var map = detector.GetLoadableTypeStaleMap(CatalogItemId);
        SmartConLogger.Info(
            $"Per-type check: IsStale={verdict.IsStale} Reason={verdict.Reason} " +
            $"map=[{(map is null ? "<null>" : string.Join(",", map.Select(kv => $"{kv.Key}={kv.Value}")))}]");
        await Assert.That(verdict.IsStale).IsTrue();
        await Assert.That(verdict.Reason).IsEqualTo(StaleReason.ContentDrift);
        await Assert.That(map).IsNotNull();
        await Assert.That(map!["TypeA"]).IsTrue();
        await Assert.That(map["TypeB"]).IsFalse();
    }

    [Test]
    public async Task Check_ContentMatches_PerTypeMapAbsent()
    {
        // No local edits — the verdict is "not stale" and NO per-type map
        // is stored (the tree falls back to the leaf verdict).
        var projectDoc = SeedProjectWithChild();
        var detector = CreateDetector(projectDoc, out var store);
        WriteCurrentMarker(projectDoc, store);

        var results = await detector.CheckCategoryAsync(null, projectDoc, CancellationToken.None);

        var verdict = results.First(r => string.Equals(r.FamilyName, ChildName, StringComparison.OrdinalIgnoreCase));
        await Assert.That(verdict.IsStale).IsFalse();
        await Assert.That(detector.GetLoadableTypeStaleMap(CatalogItemId)).IsNull();
    }

    [Test]
    public async Task Check_DriftThenReverted_PerTypeMapCleared()
    {
        // Drift produces a map; reverting the edit and re-checking must
        // CLEAR it (a lingering map would paint ghost dots forever).
        var projectDoc = SeedProjectWithChild();
        var detector = CreateDetector(projectDoc, out var store);
        WriteCurrentMarker(projectDoc, store);

        EditEmbeddedChildTypeValueLocally(projectDoc, "TypeA", 99.0);
        var driftResults = await detector.CheckCategoryAsync(null, projectDoc, CancellationToken.None);
        await Assert.That(driftResults.First(r => r.FamilyName == ChildName).IsStale).IsTrue();
        await Assert.That(detector.GetLoadableTypeStaleMap(CatalogItemId)).IsNotNull();

        EditEmbeddedChildTypeValueLocally(projectDoc, "TypeA", 1.0);
        var healedResults = await detector.CheckCategoryAsync(null, projectDoc, CancellationToken.None);

        await Assert.That(healedResults.First(r => r.FamilyName == ChildName).IsStale).IsFalse();
        await Assert.That(detector.GetLoadableTypeStaleMap(CatalogItemId)).IsNull();
    }

    [Test]
    public async Task Check_PartialTypeSet_PerTypeMapCoversIntersectionOnly()
    {
        // Only TypeA loaded in the project (preserve-types subset, #101).
        // A local edit of TypeA is flagged; TypeB (not loaded) never
        // enters the map — the type-set rule compares the intersection.
        var projectDoc = SeedProjectWithChildTypeAOnly();
        var detector = CreateDetector(projectDoc, out var store);
        WriteCurrentMarker(projectDoc, store);

        EditEmbeddedChildTypeValueLocally(projectDoc, "TypeA", 99.0);

        var results = await detector.CheckCategoryAsync(null, projectDoc, CancellationToken.None);

        var verdict = results.First(r => string.Equals(r.FamilyName, ChildName, StringComparison.OrdinalIgnoreCase));
        var map = detector.GetLoadableTypeStaleMap(CatalogItemId);
        await Assert.That(verdict.IsStale).IsTrue();
        await Assert.That(map).IsNotNull();
        await Assert.That(map!["TypeA"]).IsTrue();
        await Assert.That(map.ContainsKey("TypeB")).IsFalse();
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private int CurrentRevitMajor => int.Parse(Application.VersionNumber);

    private Document SeedProjectWithChild()
    {
        var projectDoc = SampleFiles.NewMepTemplateDocument(Application);
        if (projectDoc is null)
        {
            Skip.Test("Project templates (.rte) not found — cannot seed the per-type contract");
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

    private Document SeedProjectWithChildTypeAOnly()
    {
        var projectDoc = SampleFiles.NewMepTemplateDocument(Application);
        if (projectDoc is null)
        {
            Skip.Test("Project templates (.rte) not found — cannot seed the per-type contract");
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

    private StaleDetector CreateDetector(Document projectDoc, out RevitFamilyVersionStore store)
    {
        var context = new StubRevitContext(projectDoc);
        store = new RevitFamilyVersionStore(new RevitTransactionService(context));
        return new StaleDetector(
            store,
            new StubCatalogProvider(new[] { CreateItem() }),
            new InlineAwaitableEvent(),
            context,
            new StubClock(),
            new NullSystemTypeFinder(),
            new NullSystemTypeVersionStore(),
            new NullFamilyTypeRepository(),
            fileResolver: new StubFileResolver(_childPath!, "v1"),
            snapshotExtractor: new RevitFamilySnapshotExtractor(),
            contentHasher: new FamilyContentHasher(),
            versionWriter: null);
    }

    private void WriteCurrentMarker(Document projectDoc, RevitFamilyVersionStore store)
    {
        var nested = FindNestedFamily(projectDoc, ChildName);
        store.WriteToLoadedFamily(projectDoc, nested.Id,
            new FamilyVersion(1, CatalogItemId, "v1", DateTimeOffset.UtcNow, CurrentRevitMajor));
    }

    private static FamilyCatalogItem CreateItem()
    {
        return new FamilyCatalogItem(
            Id: CatalogItemId,
            Name: ChildName,
            NormalizedName: ChildName.ToUpperInvariant(),
            Description: null,
            CategoryPath: null,
            CategoryId: null,
            Manufacturer: null,
            ContentStatus: ContentStatus.Active,
            CurrentVersionLabel: "v1",
            Tags: Array.Empty<string>(),
            PublishedBy: null,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow);
    }

    private static Family FindNestedFamily(Document doc, string familyName)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(Family))
            .Cast<Family>()
            .First(f => string.Equals(f.Name, familyName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Simulates a local edit of ONE type's parameter value inside the
    /// embedded child: switches the EditFamily copy's current type to
    /// <paramref name="typeName"/>, sets P0 and loads the copy back into
    /// the host (doc-to-doc manages its own transaction). The ES marker
    /// on the host's Family element is untouched, so the check sees
    /// "marker current, content drifted" — for exactly one type.
    /// </summary>
    private void EditEmbeddedChildTypeValueLocally(Document hostDoc, string typeName, double newValue)
    {
        var nested = FindNestedFamily(hostDoc, ChildName);
        var copy = hostDoc.EditFamily(nested);
        try
        {
            using (var tx = new Transaction(copy, "Local type edit"))
            {
                tx.Start();
                var p0 = copy.FamilyManager.GetParameters()
                    .First(x => string.Equals(x.Definition?.Name, "P0", StringComparison.Ordinal));
                var target = copy.FamilyManager.Types.Cast<FamilyType>()
                    .First(t => string.Equals(t.Name, typeName, StringComparison.Ordinal));
                copy.FamilyManager.CurrentType = target;
                copy.FamilyManager.Set(p0, newValue);
                tx.Commit();
            }
            var pushed = copy.LoadFamily(hostDoc, new OverwriteLoadOptions());
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
