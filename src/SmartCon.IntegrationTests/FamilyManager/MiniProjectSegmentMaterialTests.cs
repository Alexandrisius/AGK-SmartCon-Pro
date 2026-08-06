using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Logging;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// E4 (#211): зонд → регрессионный тест. Проверяет, что материал pipe-сегмента
/// переживает создание мини-проекта (<c>ElementTransformUtils.CopyElements</c>
/// типа трубы должен транзитивно тянуть segment → material). Если материал в
/// мини-проекте теряется (<c>MaterialId == InvalidElementId</c>), sync позже
/// падает с "material '&lt;none&gt;' could not be resolved" — root cause тогда
/// в staging, а не в sync-политике. PROBE-строки в логе показывают ВСЕ сегменты
/// мини-проекта с их материалами.
/// </summary>
public sealed class MiniProjectSegmentMaterialTests : RevitApiTest
{
    private const string ProbeMaterialName = "SmartCon Probe Material";
    private const string ProbeScheduleName = "SmartCon Probe Schedule";
    private const string ProbeSegmentName = "SmartCon Probe Segment";
    private const string ProbeTypeName = "SmartCon Probe Pipe";

    private Document? _sourceDoc;
    private string? _tempDir;
    private string? _miniProjectPath;
    private bool _staged;

    private Document SourceDoc => _sourceDoc!;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CreateDocuments()
    {
        _sourceDoc = Application.NewProjectDocument(UnitSystem.Metric);
        _tempDir = Path.Combine(Path.GetTempPath(), $"SmartConSegMat_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _miniProjectPath = Path.Combine(_tempDir, "probe.rvt");

        var tx = new RevitTransactionService(new StubRevitContext(SourceDoc));
        ElementId? probeSegmentId = null;
        var seeded = tx.RunInTransaction(SourceDoc, "Seed probe pipe type", doc =>
        {
            var materialId = Material.Create(doc, ProbeMaterialName);
            var schedule = PipeScheduleType.Create(doc, ProbeScheduleName);
            const double diameterFt = 0.0492126; // 15 мм
            var sizes = new List<MEPSize>
            {
                new(diameterFt, diameterFt * 0.9, diameterFt, true, true),
            };
            var segment = PipeSegment.Create(doc, materialId, schedule.Id, sizes);
            try { segment.Name = ProbeSegmentName; } catch { /* имя информационно */ }
            probeSegmentId = segment.Id;

            var templatePipeType = new FilteredElementCollector(doc)
                .OfClass(typeof(PipeType))
                .Cast<PipeType>()
                .FirstOrDefault();
            if (templatePipeType is null) return;

            var probeType = (PipeType)templatePipeType.Duplicate(ProbeTypeName);
            using var manager = probeType.RoutingPreferenceManager;
            for (var i = manager.GetNumberOfRules(RoutingPreferenceRuleGroupType.Segments) - 1; i >= 0; i--)
            {
                manager.RemoveRule(RoutingPreferenceRuleGroupType.Segments, i);
            }
            manager.AddRule(
                RoutingPreferenceRuleGroupType.Segments,
                new RoutingPreferenceRule(segment.Id, "probe"));
        });

        if (!seeded || probeSegmentId is null)
        {
            Skip.Test("Не удалось посеять probe PipeType с сегментом и материалом");
            return;
        }

        var probeTypeUniqueId = SourceDoc.GetElement(
            new FilteredElementCollector(SourceDoc)
                .OfClass(typeof(PipeType))
                .Cast<PipeType>()
                .First(t => string.Equals(t.Name, ProbeTypeName, StringComparison.Ordinal)).Id
        ).UniqueId;

        var ops = new SystemFamilyRevitOperations(
            null!, tx, new LoadableFamilyScanner(),
            new RevitMiniProjectMarker(tx, new SmartCon.Core.Services.Interfaces.SystemClock()));
        var staged = ops.CreateCleanProjectWithTypesAndInstances(
            SourceDoc,
            new[] { probeTypeUniqueId },
            BuiltInCategory.OST_PipeCurves,
            "Трубы",
            _miniProjectPath);

        if (!staged.Success || !File.Exists(_miniProjectPath))
        {
            Skip.Test($"Мини-проект не создан: {staged.Error ?? "unknown"}");
            return;
        }
        _staged = true;
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CloseDocuments()
    {
        _sourceDoc?.Close(false);
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
    public async Task CreateCleanProject_PipeSegmentMaterial_SurvivesIntoMiniProject()
    {
        if (!_staged)
        {
            Skip.Test("Staging не выполнен (см. Before-хук)");
            return;
        }

        var miniDoc = Application.OpenDocumentFile(_miniProjectPath!);
        try
        {
            var segments = new FilteredElementCollector(miniDoc)
                .OfClass(typeof(PipeSegment))
                .Cast<PipeSegment>()
                .ToList();
            foreach (var s in segments)
            {
                var materialName = s.MaterialId is not null && s.MaterialId != ElementId.InvalidElementId
                    ? miniDoc.GetElement(s.MaterialId)?.Name ?? "<missing element>"
                    : "<none>";
                SmartConLogger.Info($"PROBE: mini-project segment '{s.Name}' material='{materialName}'");
            }

            var probe = segments.FirstOrDefault(
                s => string.Equals(s.Name, ProbeSegmentName, StringComparison.Ordinal));
            await Assert.That(probe).IsNotNull();

            using (Assert.Multiple())
            {
                await Assert.That(probe!.MaterialId).IsNotNull();
                await Assert.That(probe.MaterialId).IsNotEqualTo(ElementId.InvalidElementId);
            }

            var material = miniDoc.GetElement(probe.MaterialId);
            await Assert.That(material?.Name).IsEqualTo(ProbeMaterialName);
        }
        finally
        {
            miniDoc.Close(false);
        }
    }
}
