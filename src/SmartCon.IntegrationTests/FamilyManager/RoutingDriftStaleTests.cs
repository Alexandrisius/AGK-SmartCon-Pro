using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.Stale;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;
using TUnit.Core;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// ADR-072 World B: the stale check upgrades a marker-current system family
/// to <see cref="StaleReason.RoutingDrift"/> when the LIVE routing of the
/// project type differs from the catalog's item-level routing links
/// (RoutingFingerprint comparison), and stays quiet when they match.
/// </summary>
public sealed class RoutingDriftStaleTests : PipeModelFixture
{
    private const string ItemId = "pipes-drift";

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task CheckSystemFamily_LiveRoutingDiffersFromCatalogLinks_RoutingDrift()
    {
        var typeName = GetFirstPipeTypeName();
        var detector = CreateDetector(typeName, catalogRules: true);

        var result = await detector.CheckSystemFamilyAsync(ItemId, "Pipes", Doc, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.IsStale).IsTrue();
        await Assert.That(result.Reason).IsEqualTo(StaleReason.RoutingDrift);
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task CheckSystemFamily_CatalogLinksMatchLive_NotStale()
    {
        // The catalog stores EXACTLY the live type's routing (default pipe
        // types carry segment rules) — fingerprints equal, no drift.
        var pipe = Doc.GetElement(FirstPipeId) as Pipe;
        var live = new RevitFamilySnapshotExtractor()
            .ExtractSystemTypeRouting(Doc, pipe!.PipeType.Id);
        await Assert.That(live).IsNotNull();

        var rules = new List<FamilyRoutingRuleInfo>();
        var settings = new List<FamilyRoutingTypeSettings>();
        RoutingRuleRecordMapper.ToRecords(
            new SystemTypeSnapshot(pipe.PipeType.Name, [], Routing: live,
                FamilyName: "Pipe Types", FamilyKey: "Single"),
            rules, settings);
        var detector = CreateDetectorWith(pipe.PipeType.Name, rules, settings);

        var result = await detector.CheckSystemFamilyAsync(ItemId, "Pipes", Doc, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.IsStale).IsFalse();
        await Assert.That(result.Reason).IsEqualTo(StaleReason.None);
    }

    private string GetFirstPipeTypeName()
    {
        var pipe = Doc.GetElement(FirstPipeId) as Pipe;
        return pipe!.PipeType.Name;
    }

    private StaleDetector CreateDetector(string typeName, bool catalogRules)
    {
        var rules = new List<FamilyRoutingRuleInfo>();
        if (catalogRules)
        {
            rules.Add(new FamilyRoutingRuleInfo(
                typeName, "Single", "Elbows", 0, "Elbow:X", string.Empty, []));
        }
        var settings = new List<FamilyRoutingTypeSettings>
        {
            new(typeName, "Single", 0),
        };
        return CreateDetectorWith(typeName, rules, settings);
    }

    private StaleDetector CreateDetectorWith(
        string typeName,
        IReadOnlyList<FamilyRoutingRuleInfo> rules,
        IReadOnlyList<FamilyRoutingTypeSettings> settings)
    {
        var item = new FamilyCatalogItem(
            ItemId, "Pipes", "PIPES", null, null, null, null,
            ContentStatus.Active, "v1", [], null,
            default, default,
            FamilySource: "system",
            RevitCategoryId: -2008044);

        var versionStore = new RevitFamilyVersionStore(
            new RevitTransactionService(new StubRevitContext(Doc)));
        return new StaleDetector(
            versionStore,
            new StubCatalogProvider([item]),
            new InlineAwaitableEvent(),
            new StubRevitContext(Doc),
            new StubClock(),
            new RevitSystemTypeFinder(),
            versionStore,
            new InlineTypeRepository(typeName),
            snapshotExtractor: new RevitFamilySnapshotExtractor(),
            routingRuleRepository: new InlineRoutingRuleRepository(rules, settings));
    }

    private sealed class InlineTypeRepository : IFamilyTypeRepository
    {
        private readonly string _typeName;

        public InlineTypeRepository(string typeName) => _typeName = typeName;

        private IReadOnlyList<FamilyTypeDescriptor> Descriptors(string catalogItemId) =>
        [
            new FamilyTypeDescriptor(
                "t1", catalogItemId, _typeName, 0,
                FamilyName: "Pipe Types", FamilyKey: "Single"),
        ];

        public Task<IReadOnlyList<FamilyTypeDescriptor>> GetTypesForItemAsync(
            string catalogItemId, CancellationToken ct = default)
            => Task.FromResult(Descriptors(catalogItemId));

        public Task<IReadOnlyList<FamilyTypeDescriptor>> GetTypesForItemVersionAsync(
            string catalogItemId, string? versionId, CancellationToken ct = default)
            => Task.FromResult(Descriptors(catalogItemId));

        public Task<IReadOnlyDictionary<string, IReadOnlyList<FamilyTypeDescriptor>>> GetAllTypesBatchAsync(
            IEnumerable<string> catalogItemIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<FamilyTypeDescriptor>>>(
                new Dictionary<string, IReadOnlyList<FamilyTypeDescriptor>>());

        public Task<IReadOnlyDictionary<string, string>> SyncTypesAsync(
            string catalogItemId, string? versionId, string? fileId, string runId,
            IReadOnlyList<FamilyTypeDescriptor> types, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task<bool> HasTypesAsync(string catalogItemId, CancellationToken ct = default)
            => Task.FromResult(true);
    }

    private sealed class InlineRoutingRuleRepository : IFamilyRoutingRuleRepository
    {
        private readonly IReadOnlyList<FamilyRoutingRuleInfo> _rules;
        private readonly IReadOnlyList<FamilyRoutingTypeSettings> _settings;

        public InlineRoutingRuleRepository(
            IReadOnlyList<FamilyRoutingRuleInfo> rules,
            IReadOnlyList<FamilyRoutingTypeSettings> settings)
        {
            _rules = rules;
            _settings = settings;
        }

        public Task<bool> HasAnyForItemAsync(string catalogItemId, CancellationToken ct = default)
            => Task.FromResult(_rules.Count > 0 || _settings.Count > 0);

        public Task<IReadOnlyList<RoutingPartReference>> ReadAllPartReferencesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RoutingPartReference>>(Array.Empty<RoutingPartReference>());

        public Task<(IReadOnlyList<FamilyRoutingRuleInfo>, IReadOnlyList<FamilyRoutingTypeSettings>)> ReadForItemAsync(
            string catalogItemId, CancellationToken ct = default)
            => Task.FromResult((_rules, _settings));

        public Task ReplaceForItemAsync(string catalogItemId,
            IReadOnlyList<FamilyRoutingRuleInfo> rules,
            IReadOnlyList<FamilyRoutingTypeSettings> settings, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task MarkCurrentVersionRoutingBackfilledAsync(string catalogItemId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ReplaceForVersionAsync(string catalogItemId, string catalogVersionId,
            IReadOnlyList<FamilyRoutingRuleInfo> rules, IReadOnlyList<FamilyRoutingTypeSettings> settings,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task<(IReadOnlyList<FamilyRoutingRuleInfo>, IReadOnlyList<FamilyRoutingTypeSettings>)> ReadForVersionAsync(
            string catalogItemId, string catalogVersionId, CancellationToken ct = default)
            => Task.FromResult((_rules, _settings));

        public Task<bool> HasRulesForVersionAsync(string catalogItemId, string catalogVersionId, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task ReplaceForCurrentVersionAsync(string catalogItemId,
            IReadOnlyList<FamilyRoutingRuleInfo> rules, IReadOnlyList<FamilyRoutingTypeSettings> settings,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task<(IReadOnlyList<FamilyRoutingRuleInfo>, IReadOnlyList<FamilyRoutingTypeSettings>)> ReadForCurrentVersionAsync(
            string catalogItemId, CancellationToken ct = default)
            => Task.FromResult((_rules, _settings));

        public Task<bool> HasRulesForCurrentVersionAsync(string catalogItemId, CancellationToken ct = default)
            => Task.FromResult(true);
    }
}
