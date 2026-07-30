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
/// MEP routing sync (Issue #104, Phase 1): сегменты с таблицами размеров,
/// материалы и правила трассировки синхронизируются из мини-проекта в проект
/// без дубликатов; используемые размеры не удаляются и попадают в счётчик
/// not-converged.
/// </summary>
public sealed class SystemTypeMepSyncTests : RevitApiTest
{
    private const string TypeName = "SmartCon Routed Pipe";
    private const string SegmentName = "SmartCon Sync Segment";
    private const string ScheduleName = "SmartCon Sync Schedule";
    private const string MaterialName = "SmartCon Sync Steel";

    private static double Dn25 => 25.0 / 304.8;
    private static double Dn50 => 50.0 / 304.8;
    private static double Dn100 => 100.0 / 304.8;

    private Document? _sourceDoc;
    private Document? _targetDoc;
    private RevitTransactionService? _sourceTx;
    private RevitTransactionService? _targetTx;
    private SystemTypeSyncService? _syncService;
    private RevitSystemTypeFinder? _finder;

    private Document SourceDoc => _sourceDoc!;
    private Document TargetDoc => _targetDoc!;
    private SystemTypeSyncService SyncService => _syncService!;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CreateDocuments()
    {
        _sourceDoc = Application.NewProjectDocument(UnitSystem.Metric);
        _targetDoc = Application.NewProjectDocument(UnitSystem.Metric);
        _sourceTx = new RevitTransactionService(new StubRevitContext(SourceDoc));
        _targetTx = new RevitTransactionService(new StubRevitContext(TargetDoc));
        _finder = new RevitSystemTypeFinder();
        var materialSync = new RevitMaterialSyncService();
        var segmentSync = new RevitSegmentSyncService(materialSync);
        _syncService = new SystemTypeSyncService(
            _targetTx, new RevitFamilySnapshotExtractor(), _finder, new SystemClock(),
            materialSync, segmentSync, new NullFittingDependencyResolver(),
            new RevitCompoundStructureSyncService(materialSync));

        var seeded = false;
        _sourceTx.RunInTransaction(SourceDoc, "Seed reference routing", doc =>
        {
            var pipeType = new FilteredElementCollector(doc)
                .OfClass(typeof(PipeType))
                .Cast<PipeType>()
                .FirstOrDefault();
            if (pipeType is null) return;

            var material = new FilteredElementCollector(doc)
                .OfClass(typeof(Material))
                .Cast<Material>()
                .FirstOrDefault();
            if (material is null) return;

            var referenceMaterial = material.Duplicate(MaterialName);
            referenceMaterial.Color = new Color(200, 30, 30);
            referenceMaterial.Transparency = 40;

            var schedule = PipeScheduleType.Create(doc, ScheduleName);
            var sizes = new List<MEPSize>
            {
                new(Dn25, Dn25 * 0.9, Dn25, true, true),
                new(Dn50, Dn50 * 0.9, Dn50, true, true),
            };
            var segment = PipeSegment.Create(doc, referenceMaterial.Id, schedule.Id, sizes);
            if (!string.Equals(segment.Name, SegmentName, StringComparison.Ordinal))
            {
                try { segment.Name = SegmentName; } catch { /* informational */ }
            }

            var type = (ElementType)pipeType.Duplicate(TypeName);
            if (type is MEPCurveType mepType)
            {
                using var manager = mepType.RoutingPreferenceManager;
                for (var i = manager.GetNumberOfRules(RoutingPreferenceRuleGroupType.Segments) - 1; i >= 0; i--)
                {
                    manager.RemoveRule(RoutingPreferenceRuleGroupType.Segments, i);
                }
                var actualSegment = new FilteredElementCollector(doc)
                    .OfClass(typeof(Segment))
                    .Cast<Segment>()
                    .First(s => string.Equals(s.Name, SegmentName, StringComparison.OrdinalIgnoreCase));
                manager.AddRule(
                    RoutingPreferenceRuleGroupType.Segments,
                    new RoutingPreferenceRule(actualSegment.Id, "reference segment"));
            }
            seeded = true;
        });

        if (!seeded)
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
    public async Task Routing_NewType_SegmentMaterialAndRulesRebuilt()
    {
        var result = SyncService.SyncTypeFromSource(
            SourceDoc, TargetDoc, TypeName, "item-mep", "v1",
            int.Parse(Application.VersionNumber));

        using (Assert.Multiple())
        {
            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.Status).IsEqualTo(SystemTypeSyncStatus.Created);
            await Assert.That(result.NotConvergedCount).IsEqualTo(0);
        }

        // Сегмент создан в проекте с эталонной таблицей размеров.
        var segment = FindSegment(TargetDoc, SegmentName);
        using (Assert.Multiple())
        {
            await Assert.That(segment).IsNotNull();
            var sizes = segment!.GetSizes().Select(s => s.NominalDiameter).OrderBy(d => d).ToList();
            await Assert.That(sizes.Count).IsEqualTo(2);
            await Assert.That(sizes[0]).IsEqualTo(Dn25);
            await Assert.That(sizes[1]).IsEqualTo(Dn50);
        }

        // Материал создан с эталонными свойствами.
        var material = FindMaterial(TargetDoc, MaterialName);
        using (Assert.Multiple())
        {
            await Assert.That(material).IsNotNull();
            await Assert.That(material!.Color.Red).IsEqualTo((byte)200);
            await Assert.That(material.Transparency).IsEqualTo(40);
        }

        // Правила трассировки типа указывают на сегмент проекта.
        var typeId = _finder!.FindTypeByName(TargetDoc, TypeName, null);
        var type = (MEPCurveType)TargetDoc.GetElement(typeId!)!;
        using var manager = type.RoutingPreferenceManager;
        using (Assert.Multiple())
        {
            await Assert.That(manager.GetNumberOfRules(RoutingPreferenceRuleGroupType.Segments)).IsEqualTo(1);
            var rule = manager.GetRule(RoutingPreferenceRuleGroupType.Segments, 0);
            var routedSegment = TargetDoc.GetElement(rule.MEPPartId) as Segment;
            await Assert.That(routedSegment?.Name).IsEqualTo(SegmentName);
        }
    }

    [Test]
    public async Task Routing_ExistingSegment_SizeTableConvergesWithoutDuplicates()
    {
        // В проекте уже есть сегмент с таким именем, но другой таблицей
        // размеров: DN25 (общий) + DN100 (нет в эталоне, не используется).
        _targetTx!.RunInTransaction(TargetDoc, "Seed local segment", doc =>
        {
            var material = new FilteredElementCollector(doc)
                .OfClass(typeof(Material))
                .Cast<Material>()
                .First();
            var schedule = PipeScheduleType.Create(doc, ScheduleName);
            var sizes = new List<MEPSize>
            {
                new(Dn25, Dn25, Dn25, true, true),
                new(Dn100, Dn100, Dn100, true, true),
            };
            var segment = PipeSegment.Create(doc, material.Id, schedule.Id, sizes);
            try { segment.Name = SegmentName; } catch { }
        });

        var result = SyncService.SyncTypeFromSource(
            SourceDoc, TargetDoc, TypeName, "item-mep", "v1",
            int.Parse(Application.VersionNumber));

        var segment = FindSegment(TargetDoc, SegmentName);
        using (Assert.Multiple())
        {
            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(segment).IsNotNull();

            // DN100 удалён (не используется), DN50 добавлен, DN25 сохранён.
            var sizes = segment!.GetSizes().Select(s => s.NominalDiameter).OrderBy(d => d).ToList();
            await Assert.That(sizes.Count).IsEqualTo(2);
            await Assert.That(sizes[0]).IsEqualTo(Dn25);
            await Assert.That(sizes[1]).IsEqualTo(Dn50);

            // Ноль дубликатов сегментов/спецификаций.
            await Assert.That(CountSegments(TargetDoc, SegmentName)).IsEqualTo(1);
            await Assert.That(CountSchedules(TargetDoc, ScheduleName)).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Routing_UsedSize_IsNotRemovedAndReportedNotConverged()
    {
        // Сегмент с лишним размером DN100 + размещённая труба с этим размером.
        // Труба может использовать сегмент, только если он входит в правила
        // трассировки её типа — поэтому сидируем отдельный тип трубы.
        var seedOk = false;
        _targetTx!.RunInTransaction(TargetDoc, "Seed used size", doc =>
        {
            var material = new FilteredElementCollector(doc)
                .OfClass(typeof(Material))
                .Cast<Material>()
                .First();
            var schedule = PipeScheduleType.Create(doc, ScheduleName);
            var sizes = new List<MEPSize>
            {
                new(Dn25, Dn25, Dn25, true, true),
                new(Dn100, Dn100, Dn100, true, true),
            };
            var segment = PipeSegment.Create(doc, material.Id, schedule.Id, sizes);
            try { segment.Name = SegmentName; } catch { }
            var segmentId = new FilteredElementCollector(doc)
                .OfClass(typeof(Segment))
                .Cast<Segment>()
                .First(s => string.Equals(s.Name, SegmentName, StringComparison.OrdinalIgnoreCase)).Id;

            var pipeType = new FilteredElementCollector(doc)
                .OfClass(typeof(PipeType))
                .Cast<PipeType>()
                .First();
            var seedType = (MEPCurveType)pipeType.Duplicate("SmartCon Seed Pipe Type");
            using (var manager = seedType.RoutingPreferenceManager)
            {
                for (var i = manager.GetNumberOfRules(RoutingPreferenceRuleGroupType.Segments) - 1; i >= 0; i--)
                {
                    manager.RemoveRule(RoutingPreferenceRuleGroupType.Segments, i);
                }
                manager.AddRule(
                    RoutingPreferenceRuleGroupType.Segments,
                    new RoutingPreferenceRule(segmentId, "seed"));
            }

            var systemType = new FilteredElementCollector(doc)
                .OfClass(typeof(PipingSystemType))
                .First();
            var level = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .First();

            var pipe = Pipe.Create(
                doc, systemType.Id, seedType.Id, level.Id, XYZ.Zero, new XYZ(3, 0, 0));
            doc.Regenerate();

            var segmentParam = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_SEGMENT_PARAM);
            var diameterParam = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
            if (segmentParam is null || diameterParam is null) return;
            if (!segmentParam.Set(segmentId)) return;
            if (!diameterParam.Set(Dn100)) return;
            seedOk = true;
        });

        if (!seedOk)
        {
            Skip.Test("Не удалось привязать размещённую трубу к сегменту/размеру — сидирование невозможно");
        }

        var result = SyncService.SyncTypeFromSource(
            SourceDoc, TargetDoc, TypeName, "item-mep", "v1",
            int.Parse(Application.VersionNumber));

        var segment = FindSegment(TargetDoc, SegmentName);
        using (Assert.Multiple())
        {
            await Assert.That(result.IsSuccess).IsTrue();
            // DN100 используется размещённой трубой — удалить нельзя, residue в счётчике.
            await Assert.That(result.NotConvergedCount).IsGreaterThanOrEqualTo(1);
            var diameters = segment!.GetSizes().Select(s => s.NominalDiameter).ToList();
            await Assert.That(diameters.Any(d => Math.Abs(d - Dn100) < 1e-9)).IsTrue();
        }
    }

    [Test]
    public async Task Material_ExistingMaterial_PropertiesUpdatedWithoutDuplicates()
    {
        _targetTx!.RunInTransaction(TargetDoc, "Seed local material", doc =>
        {
            var material = new FilteredElementCollector(doc)
                .OfClass(typeof(Material))
                .Cast<Material>()
                .First();
            var local = material.Duplicate(MaterialName);
            local.Color = new Color(10, 10, 200);
            local.Transparency = 0;
        });

        var result = SyncService.SyncTypeFromSource(
            SourceDoc, TargetDoc, TypeName, "item-mep", "v1",
            int.Parse(Application.VersionNumber));

        var material = FindMaterial(TargetDoc, MaterialName);
        using (Assert.Multiple())
        {
            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(material).IsNotNull();
            await Assert.That(material!.Color.Red).IsEqualTo((byte)200);
            await Assert.That(material.Transparency).IsEqualTo(40);

            var count = new FilteredElementCollector(TargetDoc)
                .OfClass(typeof(Material))
                .Cast<Material>()
                .Count(m => string.Equals(m.Name, MaterialName, StringComparison.OrdinalIgnoreCase));
            await Assert.That(count).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Routing_NoPartRule_RoundTripsWithoutNotConverged()
    {
        // "No part"-правило (MEPPartId = InvalidElementId) — легальный
        // контент эталона (сварные системы без фитингов): должно переноситься
        // как есть и НЕ считаться расхождением.
        _sourceTx!.RunInTransaction(SourceDoc, "Add no-part elbow rule", doc =>
        {
            var typeId = new FilteredElementCollector(doc)
                .OfClass(typeof(PipeType))
                .Cast<ElementType>()
                .First(t => string.Equals(t.Name, TypeName, StringComparison.OrdinalIgnoreCase)).Id;
            var mepType = (MEPCurveType)doc.GetElement(typeId)!;
            using var manager = mepType.RoutingPreferenceManager;
            manager.AddRule(
                RoutingPreferenceRuleGroupType.Elbows,
                new RoutingPreferenceRule(ElementId.InvalidElementId, "welded — no elbow"));
        });

        var result = SyncService.SyncTypeFromSource(
            SourceDoc, TargetDoc, TypeName, "item-mep", "v1",
            int.Parse(Application.VersionNumber));

        using (Assert.Multiple())
        {
            await Assert.That(result.IsSuccess).IsTrue();
            await Assert.That(result.NotConvergedCount).IsEqualTo(0);
        }

        var typeId2 = _finder!.FindTypeByName(TargetDoc, TypeName, null);
        var type = (MEPCurveType)TargetDoc.GetElement(typeId2!)!;
        using var targetManager = type.RoutingPreferenceManager;
        using (Assert.Multiple())
        {
            await Assert.That(targetManager.GetNumberOfRules(RoutingPreferenceRuleGroupType.Elbows))
                .IsEqualTo(1);
            var rule = targetManager.GetRule(RoutingPreferenceRuleGroupType.Elbows, 0);
            await Assert.That(rule.MEPPartId).IsEqualTo(ElementId.InvalidElementId);
        }
    }

    private static Segment? FindSegment(Document doc, string name)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(Segment))
            .Cast<Segment>()
            .FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static int CountSegments(Document doc, string name)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(Segment))
            .Cast<Segment>()
            .Count(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static int CountSchedules(Document doc, string name)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(PipeScheduleType))
            .Cast<PipeScheduleType>()
            .Count(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static Material? FindMaterial(Document doc, string name)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(Material))
            .Cast<Material>()
            .FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class NullFittingDependencyResolver : IFittingDependencyResolver
    {
        public ElementId? EnsureFitting(
            Document activeDoc, string familyName, string typeName, int targetRevitVersion) => null;
    }
}
