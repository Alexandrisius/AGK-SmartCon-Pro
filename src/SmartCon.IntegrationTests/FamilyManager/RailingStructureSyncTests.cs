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
/// ADR-065: railing structure sync — top rail height, the non-continuous
/// rail list (cleared and rebuilt from the reference) and baluster
/// placement scalars follow the reference type.
/// </summary>
public sealed class RailingStructureSyncTests : RevitApiTest
{
    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Sync_RailingType_RailListAndTopRailHeightFollow()
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

            var seeded = false;
            sourceTx.RunInTransaction(sourceDoc, "Seed railing reference", d =>
            {
                var railingType = new FilteredElementCollector(d)
                    .OfClass(typeof(RailingType)).Cast<RailingType>().FirstOrDefault();
                if (railingType is null) return;

                var reference = (RailingType)railingType.Duplicate("SC_Railing");
                reference.TopRailHeight = 1.05;
                using var structure = reference.RailStructure;
                if (structure is null) return;
                while (structure.GetNonContinuousRailCount() > 0)
                {
                    structure.RemoveNonContinuousRail(0);
                }
                structure.AddNonContinuousRail("SC_Rail_A", 0.4, 0.0);
                structure.AddNonContinuousRail("SC_Rail_B", 0.7, 0.05);
                seeded = true;
            });
            if (!seeded)
            {
                Skip.Test("В шаблоне нет RailingType — сидирование невозможно");
                return;
            }

            var result = sync.SyncTypeFromSource(
                sourceDoc, targetDoc, "SC_Railing", "item-railing", "v1",
                int.Parse(Application.VersionNumber));
            await Assert.That(result.IsSuccess).IsTrue();

            var finder = new RevitSystemTypeFinder();
            var targetTypeId = finder.FindTypeByName(targetDoc, "SC_Railing", null);
            await Assert.That(targetTypeId).IsNotNull();

            var target = (RailingType)targetDoc.GetElement(targetTypeId!)!;
            await Assert.That(target.TopRailHeight).IsEqualTo(1.05);

            using var syncedStructure = target.RailStructure;
            await Assert.That(syncedStructure).IsNotNull();
            var railNames = new List<string>();
            var railCount = syncedStructure!.GetNonContinuousRailCount();
            for (var i = 0; i < railCount; i++)
            {
                using var rail = syncedStructure.GetNonContinuousRail(i);
                railNames.Add(rail.Name);
            }

            using (Assert.Multiple())
            {
                await Assert.That(railNames).IsEquivalentTo(new[] { "SC_Rail_A", "SC_Rail_B" });
            }
        }
        finally
        {
            sourceDoc.Close(false);
            targetDoc.Close(false);
        }
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Sync_RailingType_WithoutHandrails_SucceedsWithoutResidue()
    {
        // Stress test 2026-08-04 + audit C2: a handrail-less reference
        // railing must sync cleanly — the position setters throw "The rail
        // has no primary/secondary hand rail" and used to produce false
        // NotConverged noise (guards 501c7da/e0cf9a9).
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
            var cannotRemove = false;
            sourceTx.RunInTransaction(sourceDoc, "Seed handrail-less railing", d =>
            {
                var railingType = new FilteredElementCollector(d)
                    .OfClass(typeof(RailingType)).Cast<RailingType>().FirstOrDefault();
                if (railingType is null) return;

                var reference = (RailingType)railingType.Duplicate("SC_Railing_NoHandrail");
                try
                {
                    reference.PrimaryHandrailType = ElementId.InvalidElementId;
                    reference.SecondaryHandrailType = ElementId.InvalidElementId;
                }
                catch (Autodesk.Revit.Exceptions.ArgumentException)
                {
                    cannotRemove = true;
                    return;
                }
                seeded = true;
            });
            if (cannotRemove)
            {
                Skip.Test("API не позволяет снять поручни программно на этой версии Revit");
                return;
            }
            if (!seeded)
            {
                Skip.Test("В шаблоне нет RailingType — сидирование невозможно");
                return;
            }

            var result = sync.SyncTypeFromSource(
                sourceDoc, targetDoc, "SC_Railing_NoHandrail", "item-railing", "v1",
                int.Parse(Application.VersionNumber));

            using (Assert.Multiple())
            {
                await Assert.That(result.IsSuccess).IsTrue();
                await Assert.That(result.NotConvergedCount).IsEqualTo(0);
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
            Document activeDoc, string familyName, string typeName, int targetRevitVersion) => null;
    }
}
