using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// ADR-072 (plan items 2b + 3): parameter-based routing sync of
/// manager-less MEPCurve types (flex/conduit/tray) via the full
/// <c>SyncTypeFromSource</c> path, and the catalog-DB routing substitution
/// with its legacy fallback (no stored rows → the mini routing is used).
/// </summary>
public sealed class SyncRoutingParamsFromDbTests : RevitApiTest
{
    private Document? _sourceDoc;
    private Document? _targetDoc;
    private RevitTransactionService? _targetTx;
    private RevitMaterialSyncService? _materialSync;
    private RevitSegmentSyncService? _segmentSync;

    private Document SourceDoc => _sourceDoc!;
    private Document TargetDoc => _targetDoc!;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CreateDocuments()
    {
        _sourceDoc = Application.NewProjectDocument(UnitSystem.Metric);
        _targetDoc = Application.NewProjectDocument(UnitSystem.Metric);
        _targetTx = new RevitTransactionService(new StubRevitContext(TargetDoc));
        _materialSync = new RevitMaterialSyncService();
        _segmentSync = new RevitSegmentSyncService(_materialSync);
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CloseDocuments()
    {
        _sourceDoc?.Close(false);
        _targetDoc?.Close(false);
    }

    [Test]
    public async Task Sync_FlexPipe_LegacyWiring_ParamRoutingWrittenFromSource()
    {
        var sourceFlex = FindSourceFlexPipe();
        if (sourceFlex is null) { Skip.Test("В шаблоне нет FlexPipeType"); return; }

        var sync = CreateSyncService(routingRuleRepository: null);
        var result = sync.SyncTypeFromSource(
            SourceDoc, TargetDoc, sourceFlex.Name, "item-flex", "v1",
            int.Parse(Application.VersionNumber),
            sourceFlex.FamilyName, SystemFamilyKeyResolver.Resolve(sourceFlex),
            (int)BuiltInCategory.OST_FlexPipeCurves);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.NotConvergedCount).IsEqualTo(0);

        var target = FindTargetFlexPipe(sourceFlex.Name);
        var (routingParamsNone, preferred) = ReadFlexRoutingState(target);
        // Template source: all fitting rows «Нет» — written as InvalidElementId.
        await Assert.That(routingParamsNone).IsTrue();
        // Preferred branch copied from the source extraction (template: Tee=1).
        await Assert.That(preferred).IsEqualTo(1);
    }

    [Test]
    public async Task Sync_FlexPipe_DbRoutingWinsOverMini()
    {
        var sourceFlex = FindSourceFlexPipe();
        if (sourceFlex is null) { Skip.Test("В шаблоне нет FlexPipeType"); return; }

        // Stored routing for the item's current version: preferred = 0 (Tap)
        // and every fitting row «Нет» — the DB is the routing truth, not the
        // mini-project extraction (preferred = 1).
        var repo = new FakeRoutingRuleRepository
        {
            HasRules = true,
            Settings =
            [
                new FamilyRoutingTypeSettings(sourceFlex.Name, SystemFamilyKeyResolver.Resolve(sourceFlex), 0),
            ],
            Rules =
            [
                new FamilyRoutingRuleInfo(sourceFlex.Name, SystemFamilyKeyResolver.Resolve(sourceFlex),
                    RoutingGroupKeys.ForParam("RBS_CURVETYPE_DEFAULT_TEE_PARAM"), 0, null, string.Empty, []),
            ],
        };

        var sync = CreateSyncService(repo);
        var result = sync.SyncTypeFromSource(
            SourceDoc, TargetDoc, sourceFlex.Name, "item-flex", "v1",
            int.Parse(Application.VersionNumber),
            sourceFlex.FamilyName, SystemFamilyKeyResolver.Resolve(sourceFlex),
            (int)BuiltInCategory.OST_FlexPipeCurves);

        await Assert.That(result.IsSuccess).IsTrue();

        var target = FindTargetFlexPipe(sourceFlex.Name);
        var (_, preferred) = ReadFlexRoutingState(target);
        await Assert.That(preferred).IsEqualTo(0);
    }

    [Test]
    public async Task Sync_FlexPipe_EmptyDb_LegacyFallbackKeepsMiniRouting()
    {
        var sourceFlex = FindSourceFlexPipe();
        if (sourceFlex is null) { Skip.Test("В шаблоне нет FlexPipeType"); return; }

        // Pre-V34 version: the repository answers "no stored rows" — the
        // sync must fall back to the mini-project routing (preferred = 1),
        // never erase it with an empty DB snapshot.
        var repo = new FakeRoutingRuleRepository { HasRules = false };

        var sync = CreateSyncService(repo);
        var result = sync.SyncTypeFromSource(
            SourceDoc, TargetDoc, sourceFlex.Name, "item-flex", "v1",
            int.Parse(Application.VersionNumber),
            sourceFlex.FamilyName, SystemFamilyKeyResolver.Resolve(sourceFlex),
            (int)BuiltInCategory.OST_FlexPipeCurves);

        await Assert.That(result.IsSuccess).IsTrue();

        var target = FindTargetFlexPipe(sourceFlex.Name);
        var (_, preferred) = ReadFlexRoutingState(target);
        await Assert.That(preferred).IsEqualTo(1);
    }

    private FlexPipeType? FindSourceFlexPipe()
        => new FilteredElementCollector(SourceDoc)
            .OfClass(typeof(FlexPipeType)).Cast<FlexPipeType>().FirstOrDefault();

    private FlexPipeType FindTargetFlexPipe(string name)
        => new FilteredElementCollector(TargetDoc)
            .OfClass(typeof(FlexPipeType)).Cast<FlexPipeType>()
            .First(t => string.Equals(t.Name, name, StringComparison.Ordinal));

    private static (bool RoutingParamsNone, int? Preferred) ReadFlexRoutingState(FlexPipeType type)
    {
        var allNone = true;
        int? preferred = null;
        foreach (Parameter p in type.Parameters)
        {
            if (RoutingDrivingParameters.IsPreferredBranch(p))
            {
                preferred = p.HasValue ? p.AsInteger() : null;
                continue;
            }
            if (RoutingDrivingParameters.TryGetRoutingParam(p) is not null
                && p.AsElementId() != ElementId.InvalidElementId)
            {
                allNone = false;
            }
        }
        return (allNone, preferred);
    }

    private SystemTypeSyncService CreateSyncService(IFamilyRoutingRuleRepository? routingRuleRepository)
        => new(
            _targetTx!, new RevitFamilySnapshotExtractor(), new RevitSystemTypeFinder(), new SystemClock(),
            _materialSync!, _segmentSync!, new NullFittingResolver(),
            new RevitCompoundStructureSyncService(_materialSync!),
            routingRuleRepository);

    private sealed class NullFittingResolver : IFittingDependencyResolver
    {
        public ElementId? EnsureFitting(
            Document activeDoc, string familyName, string typeName, int targetRevitVersion, string? parentCatalogItemId = null) => null;
    }

    private sealed class FakeRoutingRuleRepository : IFamilyRoutingRuleRepository
    {
        public bool HasRules { get; set; }
        public IReadOnlyList<FamilyRoutingRuleInfo> Rules { get; set; } = [];
        public IReadOnlyList<FamilyRoutingTypeSettings> Settings { get; set; } = [];

        public Task ReplaceForVersionAsync(string catalogItemId, string catalogVersionId,
            IReadOnlyList<FamilyRoutingRuleInfo> rules, IReadOnlyList<FamilyRoutingTypeSettings> settings,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task ReplaceForCurrentVersionAsync(string catalogItemId,
            IReadOnlyList<FamilyRoutingRuleInfo> rules, IReadOnlyList<FamilyRoutingTypeSettings> settings,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task<(IReadOnlyList<FamilyRoutingRuleInfo>, IReadOnlyList<FamilyRoutingTypeSettings>)> ReadForVersionAsync(
            string catalogItemId, string catalogVersionId, CancellationToken ct = default)
            => Task.FromResult((Rules, Settings));

        public Task<(IReadOnlyList<FamilyRoutingRuleInfo>, IReadOnlyList<FamilyRoutingTypeSettings>)> ReadForCurrentVersionAsync(
            string catalogItemId, CancellationToken ct = default)
            => Task.FromResult((Rules, Settings));

        public Task<bool> HasRulesForVersionAsync(string catalogItemId, string catalogVersionId, CancellationToken ct = default)
            => Task.FromResult(HasRules);

        public Task<bool> HasRulesForCurrentVersionAsync(string catalogItemId, CancellationToken ct = default)
            => Task.FromResult(HasRules);

        public Task<bool> HasAnyForItemAsync(string catalogItemId, CancellationToken ct = default)
            => Task.FromResult(HasRules);

        public Task<(IReadOnlyList<FamilyRoutingRuleInfo>, IReadOnlyList<FamilyRoutingTypeSettings>)> ReadForItemAsync(
            string catalogItemId, CancellationToken ct = default)
            => ReadForCurrentVersionAsync(catalogItemId, ct);

        public Task ReplaceForItemAsync(string catalogItemId,
            IReadOnlyList<FamilyRoutingRuleInfo> rules, IReadOnlyList<FamilyRoutingTypeSettings> settings,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task MarkCurrentVersionRoutingBackfilledAsync(string catalogItemId, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
