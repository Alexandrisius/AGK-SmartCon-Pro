using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;
using SmartCon.IntegrationTests.Support;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// Probe (баг 3 стресс-теста 2026-09-01 → FHV21): владелец изменил «диапазон
/// размера сегмента» в мини-проекте, реимпорт дал «Дубликат» — хэш не
/// изменился. Решение владельца: сегментная конфигурация (набор + диапазоны
/// + порядок) — версионный контент мини-проекта. Зонд фиксирует контракт
/// хэша системного типа (FHV21):
/// 1) список размеров сегмента (AddSize) — В хэше (SEGMENTS-секция);
/// 2) шероховатость сегмента — В хэше;
/// 3) size-критерий правила Segments (PrimarySizeCriterion min/max — те
///    самые «Мин/Макс» из диалога Routing Preferences) — В ХЭШЕ (FHV21):
///    правка диапазона в мини меняет хэш и порождает новую версию при
///    реимпорте («Дубликат» устранён в корне). Критерии ФИТИНГОВ остаются
///    вне хэша (World B — item-level, правятся во вкладке «Трассировка»).
/// </summary>
public sealed class SegmentHashProbeTests : RevitApiTest
{
    private const string SegmentName = "SmartCon HashProbe Segment";
    private const string ScheduleName = "SmartCon HashProbe Schedule";
    private const string TypeName = "SmartCon HashProbe PipeType";
    private const string MaterialName = "SmartCon HashProbe Material";

    private static double Dn25 => 25.0 / 304.8;
    private static double Dn50 => 50.0 / 304.8;
    private static double Dn80 => 80.0 / 304.8;

    private Document? _doc;
    private RevitTransactionService? _tx;
    private RevitFamilySnapshotExtractor? _extractor;
    private FamilyContentHasher? _hasher;
    private bool _seeded;

    private Document Doc => _doc!;
    private RevitTransactionService Tx => _tx!;
    private RevitFamilySnapshotExtractor Extractor => _extractor!;
    private FamilyContentHasher Hasher => _hasher!;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CreateDocument()
    {
        _doc = Application.NewProjectDocument(UnitSystem.Metric);
        _tx = new RevitTransactionService(new StubRevitContext(Doc));
        _extractor = new RevitFamilySnapshotExtractor();
        _hasher = new FamilyContentHasher();

        Tx.RunInTransaction(Doc, "Seed hash probe", doc =>
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
                new RoutingPreferenceRule(actualSegment.Id, "probe segment"));

            _seeded = true;
        });

        if (!_seeded)
        {
            Skip.Test("В шаблоне проекта нет PipeType/Material — сидирование невозможно");
        }
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CloseDocument()
    {
        _doc?.Close(false);
    }

    [Test]
    public async Task Extract_SegmentSizeListEdit_ChangesHash()
    {
        var before = ExtractHash(out var snapBefore);
        await Assert.That(snapBefore.Segments).IsNotNull();
        await Assert.That(snapBefore.Segments!.Count).IsEqualTo(1);
        await Assert.That(snapBefore.Segments[0].Sizes.Count).IsEqualTo(2);

        var edited = Tx.RunInTransaction(Doc, "Add segment size", doc =>
        {
            var segment = FindSegment(doc);
            segment.AddSize(new MEPSize(Dn80, Dn80 * 0.9, Dn80, true, true));
        });
        await Assert.That(edited).IsTrue();

        var after = ExtractHash(out var snapAfter);
        await Assert.That(snapAfter.Segments![0].Sizes.Count).IsEqualTo(3);
        await Assert.That(after).IsNotEqualTo(before);
    }

    [Test]
    public async Task Extract_SegmentRoughnessEdit_ChangesHash()
    {
        var before = ExtractHash(out _);

        var edited = Tx.RunInTransaction(Doc, "Edit segment roughness", doc =>
        {
            var segment = FindSegment(doc);
            segment.Roughness = segment.Roughness + 0.001;
        });
        await Assert.That(edited).IsTrue();

        var after = ExtractHash(out _);
        await Assert.That(after).IsNotEqualTo(before);
    }

    [Test]
    public async Task Extract_SegmentRuleSizeCriteria_ChangesHash_Fhv21()
    {
        // FHV21: «Мин/Макс» правила Segments — версионный контент мини.
        // Правка диапазона в мини МЕНЯЕТ хэш → реимпорт даёт новую версию,
        // а не «Дубликат» (исходная боль владельца устранена).
        var before = ExtractHash(out _);

        var edited = Tx.RunInTransaction(Doc, "Add segment rule criterion", doc =>
        {
            var type = FindType(doc);
            using var manager = type.RoutingPreferenceManager;
            // GetRule returns a detached copy — mutating it in place is a
            // no-op. The canonical mutation is Remove + re-Add with the
            // criterion attached (same pattern as the sync writer).
            var rule = manager.GetRule(RoutingPreferenceRuleGroupType.Segments, 0);
            manager.RemoveRule(RoutingPreferenceRuleGroupType.Segments, 0);
            rule.AddCriterion(new PrimarySizeCriterion(Dn25, Dn50));
            manager.AddRule(RoutingPreferenceRuleGroupType.Segments, rule);
        });
        await Assert.That(edited).IsTrue();

        var after = ExtractHash(out var snapAfter);
        await Assert.That(after).IsNotEqualTo(before);

        // …и экстрактор критерий ВИДИТ (он попадает и в per-version
        // хранилище family_segment_rules через импорт/backfill).
        var segRule = snapAfter.Routing?.Rules.FirstOrDefault(
            r => r.GroupType == (int)RoutingPreferenceRuleGroupType.Segments);
        await Assert.That(segRule).IsNotNull();
        await Assert.That(segRule!.Criteria.Count).IsGreaterThan(0);
        // SEGMENTS-секция снапшота несёт диапазон правила (источник хэша).
        await Assert.That(snapAfter.Segments![0].RuleMinSizeFeet).IsEqualTo(Dn25);
        await Assert.That(snapAfter.Segments[0].RuleMaxSizeFeet).IsEqualTo(Dn50);
    }

    private string ExtractHash(out SystemTypeSnapshot snapshot)
    {
        var type = FindType(Doc);
        var snap = Extractor.ExtractSingleSystemType(Doc, type.Id);
        if (snap is null)
        {
            Skip.Test($"ExtractSingleSystemType вернул null для '{TypeName}'");
        }
        snapshot = snap!;
        var familySnapshot = new SystemFamilySnapshot(
            Doc.Title, (int)BuiltInCategory.OST_PipeCurves, new[] { snapshot });
        return Hasher.ComputeForSystem(familySnapshot)?.HexString ?? string.Empty;
    }

    private static Segment FindSegment(Document doc)
        => new FilteredElementCollector(doc)
            .OfClass(typeof(Segment)).Cast<Segment>()
            .First(s => string.Equals(s.Name, SegmentName, StringComparison.OrdinalIgnoreCase));

    private static MEPCurveType FindType(Document doc)
        => new FilteredElementCollector(doc)
            .OfClass(typeof(PipeType)).Cast<MEPCurveType>()
            .First(t => string.Equals(t.Name, TypeName, StringComparison.Ordinal));
}
