using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;
using SmartCon.IntegrationTests.Support;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// E4 (#211): эталонный pipe-сегмент БЕЗ материала (MaterialId invalid в
/// источнике — доказано зондом <see cref="MiniProjectSegmentMaterialTests"/>:
/// CopyElements материалы носит, значит null = реально material-less сегмент)
/// создаётся в проекте с fallback-материалом
/// <see cref="RevitSegmentSyncService.FallbackMaterialName"/>, а не падает с
/// "material '&lt;none&gt;' could not be resolved". MaterialId у Segment
/// read-only, поэтому material-less состояние сидируется удалением материала
/// после создания сегмента.
/// </summary>
public sealed class SegmentMaterialFallbackTests : RevitApiTest
{
    private const string SegmentName = "SmartCon Materialless Segment";
    private const string TempMaterialName = "SmartCon Temp Material";
    private const string ScheduleName = "SmartCon Fallback Schedule";

    private Document? _sourceDoc;
    private Document? _targetDoc;
    private RevitTransactionService? _targetTx;
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
        _segmentSync = new RevitSegmentSyncService(new RevitMaterialSyncService());

        var sourceTx = new RevitTransactionService(new StubRevitContext(SourceDoc));
        ElementId? materialId = null;
        var created = sourceTx.RunInTransaction(SourceDoc, "Seed segment with temp material", doc =>
        {
            materialId = Material.Create(doc, TempMaterialName);
            var schedule = PipeScheduleType.Create(doc, ScheduleName);
            const double diameterFt = 0.0492126; // 15 мм
            var sizes = new List<MEPSize>
            {
                new(diameterFt, diameterFt * 0.9, diameterFt, true, true),
            };
            var segment = PipeSegment.Create(doc, materialId, schedule.Id, sizes);
            try { segment.Name = SegmentName; } catch { /* имя информационно */ }
        });
        if (!created || materialId is null)
        {
            Skip.Test("Не удалось создать probe-сегмент в исходном документе");
            return;
        }

        // Segment.MaterialId read-only (revitapidocs 2023) — material-less
        // состояние получаем удалением материала. Revit либо обнуляет ссылку,
        // либо оставляет dangling id — оба исхода дают MaterialName == null
        // в BuildSegmentSnapshot. Отказ (rollback/исключение) → Skip.
        sourceTx.RunInTransaction(SourceDoc, "Delete temp material", doc =>
        {
            try { doc.Delete(materialId); } catch { /* проверим снапшотом ниже */ }
        });

        var snapshot = _segmentSync.ReadSegment(SourceDoc, SegmentName);
        if (snapshot is null || snapshot.MaterialName is not null)
        {
            Skip.Test(
                "Revit не позволяет получить material-less сегмент " +
                $"(snapshot={(snapshot is null ? "null" : $"material='{snapshot.MaterialName}'")})");
            return;
        }
        _seeded = true;
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CloseDocuments()
    {
        _sourceDoc?.Close(false);
        _targetDoc?.Close(false);
    }

    [Test]
    public async Task SyncSegment_MaterialLessReference_CreatedWithFallbackMaterial()
    {
        if (!_seeded)
        {
            Skip.Test("Seed не выполнен (см. Before-хук)");
            return;
        }

        SmartCon.Core.Models.FamilyManager.SegmentSyncResult? result = null;
        var committed = _targetTx!.RunInTransaction(TargetDoc, "Sync material-less segment", doc =>
        {
            result = _segmentSync!.SyncSegment(SourceDoc, doc, SegmentName);
        });

        using (Assert.Multiple())
        {
            await Assert.That(committed).IsTrue();
            await Assert.That(result).IsNotNull();
            await Assert.That(result!.SegmentId).IsNotNull();
            await Assert.That(result.SizesNotConverged).IsEqualTo(0);
        }

        var segment = new FilteredElementCollector(TargetDoc)
            .OfClass(typeof(PipeSegment))
            .Cast<PipeSegment>()
            .FirstOrDefault(s => string.Equals(s.Name, SegmentName, StringComparison.Ordinal));
        await Assert.That(segment).IsNotNull();

        var material = TargetDoc.GetElement(segment!.MaterialId);
        await Assert.That(material?.Name).IsEqualTo(RevitSegmentSyncService.FallbackMaterialName);
    }
}
