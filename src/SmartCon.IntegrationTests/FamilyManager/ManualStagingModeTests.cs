using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
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
/// ADR-072 (#254): manual staging mode of <c>SystemTypeSyncService</c>
/// (<c>StageTypeFromSource</c>) — Duplicate a same-family prototype +
/// parameter writes + segment sync + SLIM routing. The staging project
/// gets no fitting rules, no ES version marker, and no duplicate materials
/// (the #254 class is absent by construction). Also covers the
/// parameter-based routing write path of manager-less types (flex/conduit/
/// tray): the slim staging forces every routing parameter to «Нет».
/// </summary>
public sealed class ManualStagingModeTests : RevitApiTest
{
    private const string TypeName = "SmartCon Stage Pipe";
    private const string SegmentName = "SmartCon Stage Segment";
    private const string ScheduleName = "SmartCon Stage Schedule";
    private const string MaterialName = "SmartCon Stage Steel";
    private const string DescriptionValue = "Staged description-42";

    private static double Dn25 => 25.0 / 304.8;
    private static double Dn50 => 50.0 / 304.8;

    private Document? _sourceDoc;
    private Document? _stagingDoc;
    private RevitTransactionService? _sourceTx;
    private SystemTypeSyncService? _syncService;

    private Document SourceDoc => _sourceDoc!;
    private Document StagingDoc => _stagingDoc!;
    private SystemTypeSyncService SyncService => _syncService!;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CreateDocuments()
    {
        _sourceDoc = Application.NewProjectDocument(UnitSystem.Metric);
        _stagingDoc = Application.NewProjectDocument(UnitSystem.Metric);
        _sourceTx = new RevitTransactionService(new StubRevitContext(SourceDoc));
        var materialSync = new RevitMaterialSyncService();
        _syncService = new SystemTypeSyncService(
            new RevitTransactionService(new StubRevitContext(StagingDoc)),
            new RevitFamilySnapshotExtractor(), new RevitSystemTypeFinder(), new SystemClock(),
            materialSync, new RevitSegmentSyncService(materialSync), new NullFittingResolver(),
            new RevitCompoundStructureSyncService(materialSync));

        var seeded = false;
        _sourceTx.RunInTransaction(SourceDoc, "Seed staging source", doc =>
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
            if (!string.Equals(segment.Name, SegmentName, StringComparison.Ordinal))
            {
                try { segment.Name = SegmentName; } catch { }
            }

            var type = (MEPCurveType)pipeType.Duplicate(TypeName);
            using (var manager = type.RoutingPreferenceManager)
            {
                for (var i = manager.GetNumberOfRules(RoutingPreferenceRuleGroupType.Segments) - 1; i >= 0; i--)
                {
                    manager.RemoveRule(RoutingPreferenceRuleGroupType.Segments, i);
                }
                var actualSegment = new FilteredElementCollector(doc)
                    .OfClass(typeof(Segment)).Cast<Segment>()
                    .First(s => string.Equals(s.Name, SegmentName, StringComparison.OrdinalIgnoreCase));
                manager.AddRule(
                    RoutingPreferenceRuleGroupType.Segments,
                    new RoutingPreferenceRule(actualSegment.Id, "reference segment"));
                // A no-part fitting rule — legal content that must survive
                // the slim staging (needs no fitting in the staging project).
                manager.AddRule(
                    RoutingPreferenceRuleGroupType.Elbows,
                    new RoutingPreferenceRule(ElementId.InvalidElementId, "welded — no elbow"));
            }

            type.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)?.Set(DescriptionValue);
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
        _stagingDoc?.Close(false);
    }

    [Test]
    public async Task Stage_PipeType_SlimRoutingAndNoDuplicateMaterials()
    {
        var result = SyncService.StageTypeFromSource(
            SourceDoc, StagingDoc, TypeName, (int)BuiltInCategory.OST_PipeCurves);

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.Status).IsEqualTo(SystemTypeSyncStatus.Created);

        var staged = new FilteredElementCollector(StagingDoc)
            .OfClass(typeof(PipeType)).Cast<PipeType>()
            .First(t => string.Equals(t.Name, TypeName, StringComparison.Ordinal));

        using (Assert.Multiple())
        {
            // Parameter write copied the source type content.
            await Assert.That(
                staged.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION)?.AsString())
                .IsEqualTo(DescriptionValue);

            // Slim routing: exactly one Segments rule (created segment), the
            // no-part elbow rule survived, nothing else.
            using var manager = staged.RoutingPreferenceManager;
            await Assert.That(manager.GetNumberOfRules(RoutingPreferenceRuleGroupType.Segments)).IsEqualTo(1);
            var segRule = manager.GetRule(RoutingPreferenceRuleGroupType.Segments, 0);
            var routedSegment = StagingDoc.GetElement(segRule.MEPPartId) as Segment;
            await Assert.That(routedSegment?.Name).IsEqualTo(SegmentName);
            await Assert.That(manager.GetNumberOfRules(RoutingPreferenceRuleGroupType.Elbows)).IsEqualTo(1);
            var elbowRule = manager.GetRule(RoutingPreferenceRuleGroupType.Elbows, 0);
            await Assert.That(elbowRule.MEPPartId).IsEqualTo(ElementId.InvalidElementId);
            await Assert.That(manager.GetNumberOfRules(RoutingPreferenceRuleGroupType.Junctions)).IsEqualTo(0);
            await Assert.That(manager.GetNumberOfRules(RoutingPreferenceRuleGroupType.Crosses)).IsEqualTo(0);
            await Assert.That(manager.GetNumberOfRules(RoutingPreferenceRuleGroupType.Transitions)).IsEqualTo(0);
            await Assert.That(manager.GetNumberOfRules(RoutingPreferenceRuleGroupType.Unions)).IsEqualTo(0);
        }

        // The segment's size table converged into the staging project.
        var stagedSegment = new FilteredElementCollector(StagingDoc)
            .OfClass(typeof(Segment)).Cast<Segment>()
            .First(s => string.Equals(s.Name, SegmentName, StringComparison.OrdinalIgnoreCase));
        var diameters = stagedSegment.GetSizes().Select(s => s.NominalDiameter).OrderBy(d => d).ToList();
        using (Assert.Multiple())
        {
            await Assert.That(diameters.Count).IsEqualTo(2);
            await Assert.That(diameters[0]).IsEqualTo(Dn25);
            await Assert.That(diameters[1]).IsEqualTo(Dn50);

            // Exactly ONE material of the reference name — the #254
            // duplicate class cannot be born without CopyElements.
            var materialCount = new FilteredElementCollector(StagingDoc)
                .OfClass(typeof(Material)).Cast<Material>()
                .Count(m => m.Name.StartsWith(MaterialName, StringComparison.Ordinal));
            await Assert.That(materialCount).IsEqualTo(1);
        }

        // No ES version marker in staging mode.
        var marker = new RevitFamilyVersionStore(
            new RevitTransactionService(new StubRevitContext(StagingDoc)))
            .ReadFromType(StagingDoc, staged.Id);
        await Assert.That(marker).IsNull();
    }

    [Test]
    public async Task Stage_FlexDuctType_RoutingParamsForcedToNone()
    {
        var sourceFlex = new FilteredElementCollector(SourceDoc)
            .OfClass(typeof(FlexDuctType)).Cast<FlexDuctType>().FirstOrDefault();
        if (sourceFlex is null) { Skip.Test("В шаблоне нет FlexDuctType"); return; }

        var result = SyncService.StageTypeFromSource(
            SourceDoc, StagingDoc, sourceFlex.Name, (int)BuiltInCategory.OST_FlexDuctCurves,
            sourceFlex.FamilyName, SystemFamilyKeyResolver.Resolve(sourceFlex));

        await Assert.That(result.IsSuccess).IsTrue();
        await Assert.That(result.NotConvergedCount).IsEqualTo(0);

        var staged = new FilteredElementCollector(StagingDoc)
            .OfClass(typeof(FlexDuctType)).Cast<FlexDuctType>()
            .First(t => string.Equals(t.Name, sourceFlex.Name, StringComparison.Ordinal));

        // Every visible routing-driving parameter is «Нет» (InvalidElementId)
        // — a custom-template prototype could carry fitting references and
        // Duplicate() would inherit them.
        var checkedCount = 0;
        foreach (Parameter p in staged.Parameters)
        {
            if (RoutingDrivingParameters.TryGetRoutingParam(p) is null)
                continue;
            checkedCount++;
            await Assert.That(p.AsElementId()).IsEqualTo(ElementId.InvalidElementId);
        }
        await Assert.That(checkedCount).IsGreaterThan(0);
    }

    private sealed class NullFittingResolver : IFittingDependencyResolver
    {
        public ElementId? EnsureFitting(
            Document activeDoc, string familyName, string typeName, int targetRevitVersion, string? parentCatalogItemId = null) => null;
    }
}
