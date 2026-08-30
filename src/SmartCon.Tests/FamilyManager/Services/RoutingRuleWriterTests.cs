using Moq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.Import;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// ADR-072 (plan items 2 + 5): <see cref="RoutingRuleWriter"/> persists the
/// final-snapshot routing per imported system parent;
/// <see cref="DependencyLinkWriter"/> augments planned links from the
/// stored routing rules (reimport from a slim mini collects none).
/// </summary>
public sealed class RoutingRuleWriterTests
{
    private const string ParentPath = "system://Трубы";
    private const string ParentItemId = "parent-1";

    [Fact]
    public async Task WriteAsync_SystemItemWithRouting_PersistsRecordsForCurrentVersion()
    {
        var (repo, calls) = CreateCapturingRepo();
        var item = MakeSystemItem(
        [
            new SystemTypeSnapshot("Pipe A", [], Routing: new RoutingPreferencesSnapshot(1,
            [
                new RoutingRuleSnapshot(0, "Seg-1", "seg", [new RoutingCriterionSnapshot("PrimarySizeCriterion", 0.04, 0.49)]),
                new RoutingRuleSnapshot(1, "Отвод:Стандарт", "elbow", []),
            ]), FamilyKey: "Pipe.Types"),
            new SystemTypeSnapshot("Wall B", [], FamilyKey: "Wall.Basic"), // routing-less type — no rows
        ]);

        await RoutingRuleWriter.WriteAsync(
            [item], new Dictionary<string, string> { [ParentPath] = ParentItemId }, repo, CancellationToken.None);

        var (itemId, rules, settings) = Assert.Single(calls);
        Assert.Equal(ParentItemId, itemId);
        Assert.Equal(2, rules.Count);
        Assert.Equal("Segments", rules[0].GroupKey);
        Assert.Equal("Elbows", rules[1].GroupKey);
        Assert.Equal("Отвод:Стандарт", rules[1].PartName);
        var setting = Assert.Single(settings);
        Assert.Equal("Pipe A", setting.TypeName);
        Assert.Equal(1, setting.PreferredJunctionType);
    }

    [Fact]
    public async Task WriteAsync_ItemAlreadyHasLinks_ReimportKeepsCuratedLinks()
    {
        var (repo, calls) = CreateCapturingRepo();
        repo.HasItemRows = true;
        var item = MakeSystemItem(
        [
            new SystemTypeSnapshot("Pipe A", [], Routing: new RoutingPreferencesSnapshot(0,
            [
                new RoutingRuleSnapshot(1, "Отвод:Стандарт", "elbow", []),
            ]), FamilyKey: "Pipe.Types"),
        ]);

        await RoutingRuleWriter.WriteAsync(
            [item], new Dictionary<string, string> { [ParentPath] = ParentItemId }, repo, CancellationToken.None);

        // World B: curated item-level links survive re-imports.
        Assert.Empty(calls);
    }

    [Fact]
    public async Task WriteAsync_NoSystemItemsOrNotImported_WritesNothing()
    {
        var (repo, calls) = CreateCapturingRepo();
        var loadable = new FamilyBatchImportItem(
            FilePath: "loadable://X", FileName: "X", RevitMajorVersion: 2025,
            Status: FamilyBatchImportStatus.New, ExistingCatalogItemId: null, FamilySource: "loadable",
            PrecomputedCatalogItemId: "x-1");

        await RoutingRuleWriter.WriteAsync(
            [loadable], new Dictionary<string, string> { ["loadable://X"] = "x-1" }, repo, CancellationToken.None);
        // System item whose parent map does NOT contain it (failed import):
        await RoutingRuleWriter.WriteAsync(
            [MakeSystemItem([])], new Dictionary<string, string>(), repo, CancellationToken.None);

        Assert.Empty(calls);
    }

    [Fact]
    public async Task DependencyLinkWriter_ReimportFromMini_LinksRebuiltFromStoredRouting()
    {
        // The collector found nothing (slim mini) — DependencyLinks is null —
        // but the stored routing rules rebuild the version's links.
        var (routingRepo, _) = CreateCapturingRepo();
        routingRepo.ReadRules =
        [
            new FamilyRoutingRuleInfo("Pipe A", "Pipe.Types", "Segments", 0, "Seg-1", "seg", []), // skipped — not a fitting
            new FamilyRoutingRuleInfo("Pipe A", "Pipe.Types", "Elbows", 0, "Отвод:Стандарт", "", []),
            new FamilyRoutingRuleInfo("Pipe A", "Pipe.Types", "Junctions", 0, null, "welded", []), // no-part — no link
            new FamilyRoutingRuleInfo("Flex A", "Flex.Round",
                RoutingGroupKeys.ForParam("RBS_CURVETYPE_DEFAULT_TEE_PARAM"), 0, "Тройник:Стандарт", "", []),
        ];
        var dependencyRepo = new CapturingDependencyRepository();
        var catalog = new Mock<IFamilyCatalogProvider>();
        catalog.Setup(c => c.FindByNormalizedNameAsync(
                FamilyNameNormalizer.Normalize("Отвод"), "loadable", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeCatalogItem("child-elbow", "Отвод", "v3"));
        catalog.Setup(c => c.FindByNormalizedNameAsync(
                FamilyNameNormalizer.Normalize("Тройник"), "loadable", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeCatalogItem("child-tee", "Тройник", "v1"));

        await DependencyLinkWriter.WriteAsync(
            [MakeSystemItem([])],
            new Dictionary<string, string> { [ParentPath] = ParentItemId },
            [],
            dependencyRepo,
            routingRepo,
            catalog.Object,
            CancellationToken.None);

        var (parentId, links) = Assert.Single(dependencyRepo.Calls);
        Assert.Equal(ParentItemId, parentId);
        Assert.Equal(2, links.Count);
        var elbow = links.Single(l => l.PartName == "Отвод:Стандарт");
        Assert.Equal("child-elbow", elbow.ChildCatalogItemId);
        Assert.Equal(FamilyDependencyKind.Routing, elbow.Kind);
        Assert.Equal("v3", elbow.ChildVersionLabel);
        var tee = links.Single(l => l.PartName == "Тройник:Стандарт");
        Assert.Equal("child-tee", tee.ChildCatalogItemId);
    }

    [Fact]
    public async Task DependencyLinkWriter_PlannedLinkWinsOverRoutingDerived()
    {
        var (routingRepo, _) = CreateCapturingRepo();
        routingRepo.ReadRules =
        [
            new FamilyRoutingRuleInfo("Pipe A", "K", "Elbows", 0, "Отвод:Стандарт", "", []),
        ];
        var dependencyRepo = new CapturingDependencyRepository();
        var catalog = new Mock<IFamilyCatalogProvider>();
        catalog.Setup(c => c.FindByNormalizedNameAsync(
                FamilyNameNormalizer.Normalize("Отвод"), "loadable", It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeCatalogItem("child-elbow", "Отвод", "v9"));
        var child = new FamilyBatchImportItem(
            FilePath: "loadable://Отвод", FileName: "Отвод", RevitMajorVersion: 2025,
            Status: FamilyBatchImportStatus.New, ExistingCatalogItemId: null, FamilySource: "loadable",
            PrecomputedCatalogItemId: "child-elbow",
            DependencyLinks: [new FamilyDependencyLink(ParentPath, FamilyDependencyKind.Routing, "Отвод:Стандарт")])
        {
            Action = FamilyBatchImportAction.IncrementVersion,
        };

        await DependencyLinkWriter.WriteAsync(
            [MakeSystemItem([]), child],
            new Dictionary<string, string> { [ParentPath] = ParentItemId },
            ["loadable://Отвод"],
            dependencyRepo,
            routingRepo,
            catalog.Object,
            CancellationToken.None);

        var (_, links) = Assert.Single(dependencyRepo.Calls);
        // Dedup by child+kind: one link, the planned one (dialog version label).
        var link = Assert.Single(links);
        Assert.Equal("child-elbow", link.ChildCatalogItemId);
        Assert.NotEqual("v9", link.ChildVersionLabel);
    }

    [Fact]
    public async Task DependencyLinkWriter_PartWithoutCatalogItem_NoLinkNoWarning()
    {
        var (routingRepo, _) = CreateCapturingRepo();
        routingRepo.ReadRules =
        [
            new FamilyRoutingRuleInfo("Pipe A", "K", "Elbows", 0, "Призрак:Стандарт", "", []),
        ];
        var dependencyRepo = new CapturingDependencyRepository();
        var catalog = new Mock<IFamilyCatalogProvider>();
        catalog.Setup(c => c.FindByNormalizedNameAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FamilyCatalogItem?)null);

        await DependencyLinkWriter.WriteAsync(
            [MakeSystemItem([])],
            new Dictionary<string, string> { [ParentPath] = ParentItemId },
            [],
            dependencyRepo,
            routingRepo,
            catalog.Object,
            CancellationToken.None);

        // The parent still gets a (empty) replace — no links, no crash.
        var (_, links) = Assert.Single(dependencyRepo.Calls);
        Assert.Empty(links);
    }

    private static FamilyBatchImportItem MakeSystemItem(IReadOnlyList<SystemTypeSnapshot> types) =>
        new(
            FilePath: ParentPath,
            FileName: "Трубы",
            RevitMajorVersion: 2025,
            Status: FamilyBatchImportStatus.New,
            ExistingCatalogItemId: null,
            FamilySource: "system",
            PrecomputedCatalogItemId: ParentItemId,
            SystemSnapshot: new SystemFamilySnapshot("Трубы", -2008044, types));

    private static FamilyCatalogItem MakeCatalogItem(string id, string name, string currentLabel) =>
        new(id, name, name.ToUpperInvariant(), null, null, null, null,
            ContentStatus.Active, currentLabel, [], null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static (FakeRoutingRuleRepository Repo, List<(string ItemId, IReadOnlyList<FamilyRoutingRuleInfo> Rules, IReadOnlyList<FamilyRoutingTypeSettings> Settings)> Calls)
        CreateCapturingRepo()
    {
        var repo = new FakeRoutingRuleRepository();
        return (repo, repo.ReplaceCalls);
    }

    private sealed class FakeRoutingRuleRepository : IFamilyRoutingRuleRepository
    {
        public List<(string, IReadOnlyList<FamilyRoutingRuleInfo>, IReadOnlyList<FamilyRoutingTypeSettings>)> ReplaceCalls = [];
        public IReadOnlyList<FamilyRoutingRuleInfo> ReadRules = [];
        public bool HasItemRows = false;

        public Task ReplaceForVersionAsync(string catalogItemId, string catalogVersionId,
            IReadOnlyList<FamilyRoutingRuleInfo> rules, IReadOnlyList<FamilyRoutingTypeSettings> settings,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task ReplaceForCurrentVersionAsync(string catalogItemId,
            IReadOnlyList<FamilyRoutingRuleInfo> rules, IReadOnlyList<FamilyRoutingTypeSettings> settings,
            CancellationToken ct = default)
        {
            ReplaceCalls.Add((catalogItemId, rules, settings));
            return Task.CompletedTask;
        }

        public Task<(IReadOnlyList<FamilyRoutingRuleInfo>, IReadOnlyList<FamilyRoutingTypeSettings>)> ReadForVersionAsync(
            string catalogItemId, string catalogVersionId, CancellationToken ct = default)
            => Task.FromResult<(IReadOnlyList<FamilyRoutingRuleInfo>, IReadOnlyList<FamilyRoutingTypeSettings>)>(
                (ReadRules, []));

        public Task<(IReadOnlyList<FamilyRoutingRuleInfo>, IReadOnlyList<FamilyRoutingTypeSettings>)> ReadForCurrentVersionAsync(
            string catalogItemId, CancellationToken ct = default)
            => Task.FromResult<(IReadOnlyList<FamilyRoutingRuleInfo>, IReadOnlyList<FamilyRoutingTypeSettings>)>(
                (ReadRules, []));

        public Task<bool> HasRulesForVersionAsync(string catalogItemId, string catalogVersionId, CancellationToken ct = default)
            => Task.FromResult(ReadRules.Count > 0);

        public Task<bool> HasRulesForCurrentVersionAsync(string catalogItemId, CancellationToken ct = default)
            => Task.FromResult(ReadRules.Count > 0);

        public Task<bool> HasAnyForItemAsync(string catalogItemId, CancellationToken ct = default)
            => Task.FromResult(HasItemRows);

        public Task<(IReadOnlyList<FamilyRoutingRuleInfo>, IReadOnlyList<FamilyRoutingTypeSettings>)> ReadForItemAsync(
            string catalogItemId, CancellationToken ct = default)
            => Task.FromResult<(IReadOnlyList<FamilyRoutingRuleInfo>, IReadOnlyList<FamilyRoutingTypeSettings>)>(
                (ReadRules, []));

        public Task ReplaceForItemAsync(string catalogItemId,
            IReadOnlyList<FamilyRoutingRuleInfo> rules, IReadOnlyList<FamilyRoutingTypeSettings> settings,
            CancellationToken ct = default)
        {
            ReplaceCalls.Add((catalogItemId, rules, settings));
            return Task.CompletedTask;
        }

        public Task MarkCurrentVersionRoutingBackfilledAsync(string catalogItemId, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class CapturingDependencyRepository : IFamilyDependencyRepository
    {
        public List<(string ParentId, IReadOnlyList<FamilyDependencyInfo> Links)> Calls = [];

        public Task ReplaceForVersionAsync(string parentCatalogItemId, string parentVersionId,
            IReadOnlyList<FamilyDependencyInfo> dependencies, CancellationToken ct = default) => Task.CompletedTask;

        public Task<int> ReplaceForCurrentVersionAsync(string parentCatalogItemId,
            IReadOnlyList<FamilyDependencyInfo> dependencies, CancellationToken ct = default)
        {
            Calls.Add((parentCatalogItemId, dependencies));
            return Task.FromResult(dependencies.Count);
        }

        public Task<IReadOnlyList<FamilyDependencyInfo>> GetForCurrentVersionAsync(string parentCatalogItemId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FamilyDependencyInfo>>([]);

        public Task<IReadOnlyList<FamilyDependencyReference>> GetReferencingParentsAsync(string childCatalogItemId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FamilyDependencyReference>>([]);

        public Task<IReadOnlyDictionary<string, IReadOnlyList<FamilyDependencyReference>>> GetReferencingParentsBatchAsync(
            IReadOnlyCollection<string> childCatalogItemIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<FamilyDependencyReference>>>(
                new Dictionary<string, IReadOnlyList<FamilyDependencyReference>>());

        public Task<IReadOnlyDictionary<string, IReadOnlyList<FamilyDependencyDrift>>> GetDependencyDriftBatchAsync(
            IReadOnlyCollection<string> parentCatalogItemIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<FamilyDependencyDrift>>>(
                new Dictionary<string, IReadOnlyList<FamilyDependencyDrift>>());
    }
}
