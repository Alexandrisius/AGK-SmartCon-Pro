using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.Stale;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// #253: per-type content-hash refinement of the SYSTEM stale map. The
/// marker-based per-type verdict (ADR-063) says "old marker = stale"; the
/// catalog's per-type content hashes answer the honest question — did THIS
/// type's content actually change between the marker's version and the
/// current one (zero document opens, mirror of the loadable DB map).
/// </summary>
public sealed class SystemPerTypeStaleDbMapTests : RevitApiTest
{
    private const string ItemId = "sys-per-type-item";
    private const string FamilyKey = "WALLTESTKEY";

    private List<Document>? _openDocs;

    private int CurrentRevitMajor => int.Parse(Application.VersionNumber);

    [Before(Test)]
    public void Seed()
    {
        _openDocs = new List<Document>();
    }

    [After(Test)]
    public void Cleanup()
    {
        if (_openDocs is null) return;
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

    [Test]
    public async Task Check_VersionMismatch_DbMapRefinesPerTypeDots()
    {
        // Both types carry v1 markers; the catalog is at v2. Only TypeB's
        // content actually changed between v1 and v2 — TypeA's old marker
        // must NOT paint its dot (updating it would be a no-op).
        var (doc, types) = SeedProjectWithTwoWallTypes();
        var detector = CreateDetector(doc, types,
            v1Hashes: [Hash(types.TypeA, "AAA"), Hash(types.TypeB, "BBB")],
            v2Hashes: [Hash(types.TypeA, "AAA"), Hash(types.TypeB, "XX2")],
            v1Sections: null, v2Sections: null,
            out _);

        var results = await detector.CheckCategoryAsync(null, doc, CancellationToken.None);

        var verdict = results.First(r => string.Equals(r.CatalogItemId, ItemId, StringComparison.Ordinal));
        var map = detector.GetSystemTypeStaleMap(ItemId);
        SmartConLogger.Info(
            $"#253 refine: IsStale={verdict.IsStale} Reason={verdict.Reason} " +
            $"map=[{(map is null ? "<null>" : string.Join(",", map.Select(kv => $"{kv.Key}={kv.Value}")))}]");
        // The item-level verdict stays marker-based (stale, VersionMismatch);
        // the per-type dots are refined.
        await Assert.That(verdict.IsStale).IsTrue();
        await Assert.That(verdict.Reason).IsEqualTo(StaleReason.VersionMismatch);
        await Assert.That(map).IsNotNull();
        await Assert.That(map![Key(types.TypeA)]).IsFalse();
        await Assert.That(map[Key(types.TypeB)]).IsTrue();
    }

    [Test]
    public async Task Check_VersionMismatch_SharedSectionChange_MarksEveryTypeStale()
    {
        // Per-type hashes are identical, but STRUCT changed — a shared
        // section affects every type (the round-5/6 rule, system side).
        var (doc, types) = SeedProjectWithTwoWallTypes();
        var detector = CreateDetector(doc, types,
            v1Hashes: [Hash(types.TypeA, "AAA"), Hash(types.TypeB, "BBB")],
            v2Hashes: [Hash(types.TypeA, "AAA"), Hash(types.TypeB, "BBB")],
            v1Sections: new Dictionary<string, string> { ["STRUCT"] = "S1", ["VALUES"] = "V1" },
            v2Sections: new Dictionary<string, string> { ["STRUCT"] = "S2", ["VALUES"] = "V1" },
            out _);

        var results = await detector.CheckCategoryAsync(null, doc, CancellationToken.None);

        var map = detector.GetSystemTypeStaleMap(ItemId);
        await Assert.That(results.First(r => r.CatalogItemId == ItemId).IsStale).IsTrue();
        await Assert.That(map).IsNotNull();
        await Assert.That(map![Key(types.TypeA)]).IsTrue();
        await Assert.That(map[Key(types.TypeB)]).IsTrue();
    }

    [Test]
    public async Task Check_VersionMismatch_AnalyticsPending_MarkerVerdictStands()
    {
        // No per-type analytics for the marker's version — the refinement
        // cannot prove anything and the marker-based dots stand (fallback).
        var (doc, types) = SeedProjectWithTwoWallTypes();
        var detector = CreateDetector(doc, types,
            v1Hashes: null, v2Hashes: null,
            v1Sections: null, v2Sections: null,
            out _);

        var results = await detector.CheckCategoryAsync(null, doc, CancellationToken.None);

        var map = detector.GetSystemTypeStaleMap(ItemId);
        await Assert.That(results.First(r => r.CatalogItemId == ItemId).IsStale).IsTrue();
        await Assert.That(map).IsNotNull();
        await Assert.That(map![Key(types.TypeA)]).IsTrue();
        await Assert.That(map[Key(types.TypeB)]).IsTrue();
    }

    // ── Infrastructure ──────────────────────────────────────────────────

    private sealed record WallTypes(FamilyTypeDescriptor TypeA, FamilyTypeDescriptor TypeB, ElementId IdA, ElementId IdB);

    private (Document Doc, WallTypes Types) SeedProjectWithTwoWallTypes()
    {
        var doc = SampleFiles.NewMepTemplateDocument(Application);
        if (doc is null)
        {
            Skip.Test("Project templates (.rte) not found — cannot seed the system per-type contracts");
            throw new InvalidOperationException("unreachable — Skip.Test throws");
        }
        _openDocs!.Add(doc);

        var wallTypes = new FilteredElementCollector(doc)
            .OfClass(typeof(WallType)).Cast<WallType>()
            .Where(w => !string.IsNullOrEmpty(w.Name))
            .GroupBy(w => w.Name, StringComparer.Ordinal)
            .Select(g => g.First())
            .Take(2)
            .ToList();
        if (wallTypes.Count < 2)
        {
            Skip.Test("The project template carries fewer than two wall types — cannot seed the contract");
            throw new InvalidOperationException("unreachable — Skip.Test throws");
        }

        var descA = new FamilyTypeDescriptor("t-a", ItemId, wallTypes[0].Name, 0, FamilyName: "Walls", FamilyKey: FamilyKey);
        var descB = new FamilyTypeDescriptor("t-b", ItemId, wallTypes[1].Name, 1, FamilyName: "Walls", FamilyKey: FamilyKey);
        return (doc, new WallTypes(descA, descB, wallTypes[0].Id, wallTypes[1].Id));
    }

    private StaleDetector CreateDetector(
        Document doc,
        WallTypes types,
        IReadOnlyList<FamilyTypeHashEntry>? v1Hashes,
        IReadOnlyList<FamilyTypeHashEntry>? v2Hashes,
        IReadOnlyDictionary<string, string>? v1Sections,
        IReadOnlyDictionary<string, string>? v2Sections,
        out StubSystemStore store)
    {
        var context = new StubRevitContext(doc);
        store = new StubSystemStore();
        var now = DateTimeOffset.UtcNow;
        store.Markers[types.IdA] = new FamilyVersion(1, ItemId, "v1", now, CurrentRevitMajor);
        store.Markers[types.IdB] = new FamilyVersion(1, ItemId, "v1", now, CurrentRevitMajor);

        var analytics = new StubAnalytics();
        if (v1Hashes is not null) analytics.TypeHashes["v1"] = v1Hashes;
        if (v2Hashes is not null) analytics.TypeHashes["v2"] = v2Hashes;
        if (v1Sections is not null) analytics.SectionHashes["v1"] = v1Sections;
        if (v2Sections is not null) analytics.SectionHashes["v2"] = v2Sections;

        var item = new FamilyCatalogItem(
            Id: ItemId,
            Name: "SysWalls",
            NormalizedName: "SYSWALLS",
            Description: null,
            CategoryPath: null,
            CategoryId: null,
            Manufacturer: null,
            ContentStatus: ContentStatus.Active,
            CurrentVersionLabel: "v2",
            Tags: Array.Empty<string>(),
            PublishedBy: null,
            CreatedAtUtc: now,
            UpdatedAtUtc: now,
            FamilySource: "system",
            RevitCategoryId: (int)BuiltInCategory.OST_Walls);

        return new StaleDetector(
            new RevitFamilyVersionStore(new RevitTransactionService(context)),
            new StubCatalogProvider(new[] { item }),
            new InlineAwaitableEvent(),
            context,
            new StubClock(),
            new StubFinder(types),
            store,
            new StubTypeRepository(types),
            contentHashAnalytics: analytics);
    }

    private static FamilyTypeHashEntry Hash(FamilyTypeDescriptor d, string hash)
        => new(SystemTypeIdentityKey.Build(d.FamilyKey, d.FamilyName, d.Name), d.Name, hash);

    private static string Key(FamilyTypeDescriptor d)
        => SystemTypeIdentityKey.Build(d.FamilyKey, d.FamilyName, d.Name);

    // ── Stubs ───────────────────────────────────────────────────────────

    private sealed class StubFinder(WallTypes types) : ISystemTypeFinder
    {
        public ElementId? FindTypeByName(
            Document doc, string typeName, int? categoryOrdinal, string? familyName = null, string? familyKey = null)
            => string.Equals(typeName, types.TypeA.Name, StringComparison.Ordinal) ? types.IdA
                : string.Equals(typeName, types.TypeB.Name, StringComparison.Ordinal) ? types.IdB
                : null;

        public IReadOnlyList<SystemTypeLocation> CollectTypes(Document doc, IReadOnlyCollection<int> categoryOrdinals)
            =>
            [
                new SystemTypeLocation(types.TypeA.Name, (int)BuiltInCategory.OST_Walls, types.IdA, "Walls", FamilyKey),
                new SystemTypeLocation(types.TypeB.Name, (int)BuiltInCategory.OST_Walls, types.IdB, "Walls", FamilyKey),
            ];
    }

    private sealed class StubSystemStore : ISystemTypeVersionStore
    {
        public Dictionary<ElementId, FamilyVersion> Markers { get; } = new();

        public FamilyVersion? ReadFromType(Document doc, ElementId typeId)
            => Markers.TryGetValue(typeId, out var v) ? v : null;

        public void WriteToType(Document doc, ElementId typeId, FamilyVersion version)
            => Markers[typeId] = version;

        public IReadOnlyDictionary<ElementId, FamilyVersion?> ReadManyFromTypes(Document doc, IEnumerable<ElementId> typeIds)
            => typeIds.ToDictionary(
                id => id,
                id => Markers.TryGetValue(id, out var v) ? v : (FamilyVersion?)null);
    }

    private sealed class StubTypeRepository(WallTypes types) : IFamilyTypeRepository
    {
        public Task<IReadOnlyList<FamilyTypeDescriptor>> GetTypesForItemAsync(string catalogItemId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FamilyTypeDescriptor>>([types.TypeA, types.TypeB]);

        public Task<IReadOnlyList<FamilyTypeDescriptor>> GetTypesForItemVersionAsync(string catalogItemId, string? versionId, CancellationToken ct = default)
            => GetTypesForItemAsync(catalogItemId, ct);

        public Task<IReadOnlyDictionary<string, IReadOnlyList<FamilyTypeDescriptor>>> GetAllTypesBatchAsync(IEnumerable<string> catalogItemIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<FamilyTypeDescriptor>>>(
                catalogItemIds.ToDictionary(id => id, _ => (IReadOnlyList<FamilyTypeDescriptor>)[types.TypeA, types.TypeB]));

        public Task<IReadOnlyDictionary<string, string>> SyncTypesAsync(
            string catalogItemId, string? versionId, string? fileId, string runId,
            IReadOnlyList<FamilyTypeDescriptor> types, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task<bool> HasTypesAsync(string catalogItemId, CancellationToken ct = default)
            => Task.FromResult(true);
    }

    private sealed class StubAnalytics : IContentHashAnalyticsRepository
    {
        public Dictionary<string, IReadOnlyList<FamilyTypeHashEntry>> TypeHashes { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyDictionary<string, string>> SectionHashes { get; } = new(StringComparer.Ordinal);

        public Task<IReadOnlyDictionary<string, string>?> GetSectionHashesAsync(string catalogItemId, string versionLabel, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, string>?>(
                SectionHashes.TryGetValue(versionLabel, out var s) ? s : null);

        public Task<IReadOnlyDictionary<string, string>?> GetSectionStringsAsync(string catalogItemId, string versionLabel, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

        public Task<IReadOnlyList<FamilyTypeHashEntry>?> GetTypeHashesAsync(string catalogItemId, string versionLabel, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FamilyTypeHashEntry>?>(
                TypeHashes.TryGetValue(versionLabel, out var t) ? t : null);
    }
}
