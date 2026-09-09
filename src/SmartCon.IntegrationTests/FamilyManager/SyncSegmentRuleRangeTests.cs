using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;
using SmartCon.IntegrationTests.Support;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// FHV21 (решение владельца 2026-09-01): sync читает правила сегментов из
/// PER-VERSION хранилища (<see cref="ISegmentRuleRepository"/>), а фитинги —
/// из item-таблиц. Диапазон активной версии применяется к живому типу
/// проекта: правило Segments после sync несёт PrimarySizeCriterion из
/// хранилища (фитинги — из routing-репозитория, доказано соседними
/// тестами).
/// </summary>
public sealed class SyncSegmentRuleRangeTests : RevitApiTest
{
    private const string SegmentName = "SmartCon RangeSync Segment";
    private const string ScheduleName = "SmartCon RangeSync Schedule";
    private const string TypeName = "SmartCon RangeSync PipeType";
    private const string MaterialName = "SmartCon RangeSync Material";

    private static double Dn25 => 25.0 / 304.8;
    private static double Dn50 => 50.0 / 304.8;

    private Document? _sourceDoc;
    private Document? _targetDoc;
    private RevitTransactionService? _targetTx;
    private RevitMaterialSyncService? _materialSync;
    private RevitSegmentSyncService? _segmentSync;
    private bool _seeded;

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

        var sourceTx = new RevitTransactionService(new StubRevitContext(SourceDoc));
        sourceTx.RunInTransaction(SourceDoc, "Seed range sync source", doc =>
        {
            var pipeType = new FilteredElementCollector(doc)
                .OfClass(typeof(PipeType)).Cast<PipeType>().FirstOrDefault();
            var material = new FilteredElementCollector(doc)
                .OfClass(typeof(Material)).Cast<Material>().FirstOrDefault();
            if (pipeType is null || material is null) return;

            var referenceMaterial = material.Duplicate(MaterialName);
            var schedule = PipeScheduleType.Create(doc, ScheduleName);
            var sizes = new List<MEPSize>
            {
                new(Dn25, Dn25 * 0.9, Dn25, true, true),
                new(Dn50, Dn50 * 0.9, Dn50, true, true),
            };
            var segment = PipeSegment.Create(doc, referenceMaterial.Id, schedule.Id, sizes);
            try { segment.Name = SegmentName; } catch { /* имя информационно */ }

            var type = (MEPCurveType)pipeType.Duplicate(TypeName);
            using var manager = type.RoutingPreferenceManager;
            for (var i = manager.GetNumberOfRules(RoutingPreferenceRuleGroupType.Segments) - 1; i >= 0; i--)
            {
                manager.RemoveRule(RoutingPreferenceRuleGroupType.Segments, i);
            }
            var actualSegment = new FilteredElementCollector(doc)
                .OfClass(typeof(Segment)).Cast<Segment>()
                .First(s => string.Equals(s.Name, SegmentName, StringComparison.OrdinalIgnoreCase));
            manager.AddRule(
                RoutingPreferenceRuleGroupType.Segments,
                new RoutingPreferenceRule(actualSegment.Id, "range rule"));

            _seeded = true;
        });

        if (!_seeded)
        {
            Skip.Test("В шаблоне проекта нет PipeType/Material — сидирование невозможно");
        }
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CloseDocuments()
    {
        _sourceDoc?.Close(false);
        _targetDoc?.Close(false);
    }

    [Test]
    public async Task Sync_PipeType_PerVersionSegmentRange_AppliedToLiveType()
    {
        var familyKey = SystemFamilyKeyResolver.Resolve(FindSourceType());
        // Каталог: фитингов нет (settings-маркер — legitimately empty fittings,
        // M14), сегмент с диапазоном DN25..DN50 в per-version хранилище.
        var routingRepo = new StubRoutingRuleRepository
        {
            HasRules = true,
            Settings = [new FamilyRoutingTypeSettings(TypeName, familyKey, 0)],
        };
        var segmentRepo = new StubSegmentRuleRepository(
        [
            new SegmentRuleRecord(TypeName, familyKey, 0, SegmentName, Dn25, Dn50, "range rule"),
        ]);

        var sync = new SystemTypeSyncService(
            _targetTx!, new RevitFamilySnapshotExtractor(), new RevitSystemTypeFinder(), new SystemClock(),
            _materialSync!, _segmentSync!, new NullFittingResolver(),
            new RevitCompoundStructureSyncService(_materialSync!),
            routingRepo, segmentRepo);
        var result = sync.SyncTypeFromSource(
            SourceDoc, TargetDoc, TypeName, "item-pipe", "v1",
            int.Parse(Application.VersionNumber),
            FindSourceType().FamilyName, familyKey,
            (int)BuiltInCategory.OST_PipeCurves);

        await Assert.That(result.IsSuccess).IsTrue();

        var target = new FilteredElementCollector(TargetDoc)
            .OfClass(typeof(PipeType)).Cast<MEPCurveType>()
            .First(t => string.Equals(t.Name, TypeName, StringComparison.Ordinal));
        using var manager = target.RoutingPreferenceManager;
        await Assert.That(manager.GetNumberOfRules(RoutingPreferenceRuleGroupType.Segments)).IsEqualTo(1);
        var rule = manager.GetRule(RoutingPreferenceRuleGroupType.Segments, 0);
        var criterion = rule.GetCriterion(0) as PrimarySizeCriterion;
        await Assert.That(criterion).IsNotNull();
        await Assert.That(criterion!.MinimumSize).IsEqualTo(Dn25);
        await Assert.That(criterion.MaximumSize).IsEqualTo(Dn50);
        // И сам сегмент доставлен в целевой проект по имени.
        var routedSegment = TargetDoc.GetElement(rule.MEPPartId) as Segment;
        await Assert.That(routedSegment?.Name).IsEqualTo(SegmentName);
    }

    private MEPCurveType FindSourceType()
        => new FilteredElementCollector(SourceDoc)
            .OfClass(typeof(PipeType)).Cast<MEPCurveType>()
            .First(t => string.Equals(t.Name, TypeName, StringComparison.Ordinal));

    private sealed class NullFittingResolver : IFittingDependencyResolver
    {
        public ElementId? EnsureFitting(
            Document activeDoc, string familyName, string typeName, int targetRevitVersion, string? parentCatalogItemId = null) => null;
    }

    private sealed class StubSegmentRuleRepository(IReadOnlyList<SegmentRuleRecord> rules)
        : ISegmentRuleRepository
    {
        public Task<IReadOnlyList<SegmentRuleRecord>> ReadForVersionAsync(
            string catalogVersionId, CancellationToken ct = default) => Task.FromResult(rules);

        public Task<IReadOnlyList<SegmentRuleRecord>> ReadForCurrentVersionAsync(
            string catalogItemId, CancellationToken ct = default) => Task.FromResult(rules);

        public Task ReplaceForVersionAsync(
            string catalogVersionId, IReadOnlyList<SegmentRuleRecord> rules, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task ReplaceForCurrentVersionAsync(
            string catalogItemId, IReadOnlyList<SegmentRuleRecord> rules, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class StubRoutingRuleRepository : IFamilyRoutingRuleRepository
    {
        public Task<IReadOnlyList<RoutingPartReference>> ReadAllPartReferencesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RoutingPartReference>>(Array.Empty<RoutingPartReference>());

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
