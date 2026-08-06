using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Services.Interfaces;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// Issue #184 (ADR-065, вариант Б): syncing a stairs type must also sync its
/// subtype references — the run/landing/support/cut-mark types are found in
/// the target by (class/category, name) or created by duplicating a
/// same-kind prototype, their own parameters are written, and the reference
/// is assigned on the target stairs type.
/// </summary>
public sealed class StairsSubtypeSyncTests : RevitApiTest
{
    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Sync_StairsType_RunSubtypeCreatedAndAssignedWithParameters()
    {
        var sourceDoc = Application.NewProjectDocument(UnitSystem.Metric);
        var targetDoc = Application.NewProjectDocument(UnitSystem.Metric);
        try
        {
            var sourceTx = new RevitTransactionService(new StubRevitContext(sourceDoc));
            var targetTx = new RevitTransactionService(new StubRevitContext(targetDoc));
            var materialSync = new RevitMaterialSyncService();
            var sync = new SystemTypeSyncService(
                targetTx, new RevitFamilySnapshotExtractor(), new RevitSystemTypeFinder(), new SystemClock(),
                materialSync, new RevitSegmentSyncService(materialSync), new NullFittingDependencyResolver(),
                new RevitCompoundStructureSyncService(materialSync));

            ElementId? sourceStairsTypeId = null;
            sourceTx.RunInTransaction(sourceDoc, "Seed stairs reference", d =>
            {
                var runType = new FilteredElementCollector(d)
                    .OfClass(typeof(StairsRunType)).Cast<StairsRunType>().FirstOrDefault();
                var stairsType = new FilteredElementCollector(d)
                    .OfClass(typeof(StairsType)).Cast<StairsType>().FirstOrDefault();
                if (runType is null || stairsType is null) return;

                var newRun = (StairsRunType)runType.Duplicate("SC_SyncRun");
                newRun.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)
                    ?.Set("run-from-reference");

                var newStairs = (StairsType)stairsType.Duplicate("SC_Stairs");
                newStairs.RunType = newRun.Id;
                sourceStairsTypeId = newStairs.Id;
            });

            if (sourceStairsTypeId is null)
            {
                Skip.Test("В шаблоне нет StairsType/StairsRunType — сидирование невозможно");
                return;
            }

            var result = sync.SyncTypeFromSource(
                sourceDoc, targetDoc, "SC_Stairs", "item-stairs", "v1",
                int.Parse(Application.VersionNumber));

            await Assert.That(result.IsSuccess).IsTrue();

            var finder = new RevitSystemTypeFinder();
            var targetTypeId = finder.FindTypeByName(targetDoc, "SC_Stairs", null);
            await Assert.That(targetTypeId).IsNotNull();

            var targetStairs = (StairsType)targetDoc.GetElement(targetTypeId!)!;
            await Assert.That(targetStairs.RunType).IsNotEqualTo(ElementId.InvalidElementId);

            var targetRun = targetDoc.GetElement(targetStairs.RunType) as StairsRunType;
            using (Assert.Multiple())
            {
                await Assert.That(targetRun).IsNotNull();
                await Assert.That(targetRun!.Name).IsEqualTo("SC_SyncRun");
                await Assert.That(
                    targetRun.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)?.AsString())
                    .IsEqualTo("run-from-reference");
            }

            // Supports live in OST_StairsStringerCarriage (NOT
            // OST_StairsSupports — that category matches no elements,
            // RevitLookup/Autodesk forums). The support type reference must
            // follow the reference by name. The source assertion is
            // UNCONDITIONAL: if the template's stairs type loses its
            // support, the test must fail loudly, not skip the check.
            var sourceStairs = (StairsType)sourceDoc.GetElement(sourceStairsTypeId)!;
            await Assert.That(sourceStairs.LeftSideSupportType)
                .IsNotEqualTo(ElementId.InvalidElementId);
            var expectedSupportName = sourceDoc.GetElement(sourceStairs.LeftSideSupportType)!.Name;
            await Assert.That(targetStairs.LeftSideSupportType)
                .IsNotEqualTo(ElementId.InvalidElementId);
            var targetSupportName = targetDoc.GetElement(targetStairs.LeftSideSupportType)!.Name;
            await Assert.That(targetSupportName).IsEqualTo(expectedSupportName);
        }
        finally
        {
            sourceDoc.Close(false);
            targetDoc.Close(false);
        }
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Sync_StairsType_SubtypeChangeInReference_UpdatesExistingTarget()
    {
        // The acceptance scenario of #184: the reference changed its run
        // type — the existing project stairs type must follow (in place).
        var sourceDoc = Application.NewProjectDocument(UnitSystem.Metric);
        var targetDoc = Application.NewProjectDocument(UnitSystem.Metric);
        try
        {
            var sourceTx = new RevitTransactionService(new StubRevitContext(sourceDoc));
            var targetTx = new RevitTransactionService(new StubRevitContext(targetDoc));
            var materialSync = new RevitMaterialSyncService();
            var sync = new SystemTypeSyncService(
                targetTx, new RevitFamilySnapshotExtractor(), new RevitSystemTypeFinder(), new SystemClock(),
                materialSync, new RevitSegmentSyncService(materialSync), new NullFittingDependencyResolver(),
                new RevitCompoundStructureSyncService(materialSync));

            var seeded = false;
            sourceTx.RunInTransaction(sourceDoc, "Seed stairs reference", d =>
            {
                var runType = new FilteredElementCollector(d)
                    .OfClass(typeof(StairsRunType)).Cast<StairsRunType>().FirstOrDefault();
                var stairsType = new FilteredElementCollector(d)
                    .OfClass(typeof(StairsType)).Cast<StairsType>().FirstOrDefault();
                if (runType is null || stairsType is null) return;

                var runV1 = (StairsRunType)runType.Duplicate("SC_Run_V1");
                var runV2 = (StairsRunType)runType.Duplicate("SC_Run_V2");
                var newStairs = (StairsType)stairsType.Duplicate("SC_Stairs2");
                newStairs.RunType = runV1.Id;
                seeded = true;
            });
            if (!seeded)
            {
                Skip.Test("В шаблоне нет StairsType/StairsRunType");
                return;
            }

            var targetSeeded = false;
            targetTx.RunInTransaction(targetDoc, "Seed target stairs", d =>
            {
                var stairsType = new FilteredElementCollector(d)
                    .OfClass(typeof(StairsType)).Cast<StairsType>().First();
                var local = (StairsType)stairsType.Duplicate("SC_Stairs2");
                targetSeeded = local.RunType is not null;
            });
            if (!targetSeeded) { Skip.Test("Не удалось сидировать целевую лестницу"); return; }

            var first = sync.SyncTypeFromSource(
                sourceDoc, targetDoc, "SC_Stairs2", "item-stairs", "v1",
                int.Parse(Application.VersionNumber));
            await Assert.That(first.IsSuccess).IsTrue();

            // Reference moves to run V2.
            sourceTx.RunInTransaction(sourceDoc, "Switch run type", d =>
            {
                var stairs = new FilteredElementCollector(d)
                    .OfClass(typeof(StairsType)).Cast<StairsType>()
                    .First(t => t.Name == "SC_Stairs2");
                var runV2 = new FilteredElementCollector(d)
                    .OfClass(typeof(StairsRunType)).Cast<StairsRunType>()
                    .First(t => t.Name == "SC_Run_V2");
                stairs.RunType = runV2.Id;
            });

            var second = sync.SyncTypeFromSource(
                sourceDoc, targetDoc, "SC_Stairs2", "item-stairs", "v2",
                int.Parse(Application.VersionNumber));
            await Assert.That(second.IsSuccess).IsTrue();

            var finder = new RevitSystemTypeFinder();
            var targetTypeId = finder.FindTypeByName(targetDoc, "SC_Stairs2", null);
            var targetStairs = (StairsType)targetDoc.GetElement(targetTypeId!)!;
            var targetRun = targetDoc.GetElement(targetStairs.RunType) as StairsRunType;
            await Assert.That(targetRun?.Name).IsEqualTo("SC_Run_V2");
        }
        finally
        {
            sourceDoc.Close(false);
            targetDoc.Close(false);
        }
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Sync_StairsType_SupportChangeInReference_UpdatesExistingTarget()
    {
        // Manual test 2026-08-04 reproduction: the project ALREADY has the
        // stairs type with both side supports = A; the reference moves the
        // right support to B (a subtype the project does not have) — the
        // existing project type must follow in place.
        var sourceDoc = Application.NewProjectDocument(UnitSystem.Metric);
        var targetDoc = Application.NewProjectDocument(UnitSystem.Metric);
        try
        {
            var sourceTx = new RevitTransactionService(new StubRevitContext(sourceDoc));
            var targetTx = new RevitTransactionService(new StubRevitContext(targetDoc));
            var materialSync = new RevitMaterialSyncService();
            var sync = new SystemTypeSyncService(
                targetTx, new RevitFamilySnapshotExtractor(), new RevitSystemTypeFinder(), new SystemClock(),
                materialSync, new RevitSegmentSyncService(materialSync), new NullFittingDependencyResolver(),
                new RevitCompoundStructureSyncService(materialSync));

            var seeded = false;
            sourceTx.RunInTransaction(sourceDoc, "Seed stairs reference", d =>
            {
                var supportProto = new FilteredElementCollector(d)
                    .WhereElementIsElementType()
                    .Cast<ElementType>()
                    .FirstOrDefault(t => t.Category is not null
                        && t.Category.Id == SmartCon.Core.Compatibility.ElementIdCompat.Create(
                            (int)BuiltInCategory.OST_StairsStringerCarriage));
                var stairsType = new FilteredElementCollector(d)
                    .OfClass(typeof(StairsType)).Cast<StairsType>().FirstOrDefault();
                if (supportProto is null || stairsType is null) return;

                var supportA = supportProto.Duplicate("SC_SupportA");
                var supportB = supportProto.Duplicate("SC_SupportB");
                var newStairs = (StairsType)stairsType.Duplicate("SC_Stairs3");
                newStairs.LeftSideSupportType = supportA.Id;
                newStairs.RightSideSupportType = supportB.Id;
                seeded = true;
            });
            if (!seeded)
            {
                Skip.Test("В шаблоне нет StairsType/support-типов — сидирование невозможно");
                return;
            }

            var targetSeeded = false;
            targetTx.RunInTransaction(targetDoc, "Seed target stairs", d =>
            {
                var stairsType = new FilteredElementCollector(d)
                    .OfClass(typeof(StairsType)).Cast<StairsType>().First();
                var local = (StairsType)stairsType.Duplicate("SC_Stairs3");
                targetSeeded = local.LeftSideSupportType is not null;
            });
            if (!targetSeeded) { Skip.Test("Не удалось сидировать целевую лестницу"); return; }

            var result = sync.SyncTypeFromSource(
                sourceDoc, targetDoc, "SC_Stairs3", "item-stairs", "v1",
                int.Parse(Application.VersionNumber));
            await Assert.That(result.IsSuccess).IsTrue();

            var finder = new RevitSystemTypeFinder();
            var targetTypeId = finder.FindTypeByName(targetDoc, "SC_Stairs3", null);
            var targetStairs = (StairsType)targetDoc.GetElement(targetTypeId!)!;
            using (Assert.Multiple())
            {
                await Assert.That(targetDoc.GetElement(targetStairs.LeftSideSupportType)?.Name)
                    .IsEqualTo("SC_SupportA");
                await Assert.That(targetDoc.GetElement(targetStairs.RightSideSupportType)?.Name)
                    .IsEqualTo("SC_SupportB");
            }
        }
        finally
        {
            sourceDoc.Close(false);
            targetDoc.Close(false);
        }
    }

    private sealed class NullFittingDependencyResolver : IFittingDependencyResolver
    {
        public ElementId? EnsureFitting(
            Document activeDoc, string familyName, string typeName, int targetRevitVersion, string? parentCatalogItemId = null) => null;
    }
}
