using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Services.Interfaces;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// FHV5 wire settings sync (manual test 2026-08-04): the material /
/// temperature rating / insulation / max size / conduit of a wire type are
/// <see cref="WireType"/> API properties backed by the ElectricalSetting
/// object graph — they never appear in Element.Parameters, so the generic
/// parameter pipeline cannot sync them. A material missing in the project
/// is created by duplicating an existing one.
/// </summary>
public sealed class WireSettingsSyncTests : RevitApiTest
{
    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Sync_WireType_MaterialChangeInReference_CreatedAndAssignedInTarget()
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
            sourceTx.RunInTransaction(sourceDoc, "Seed wire reference", d =>
            {
                var wireType = new FilteredElementCollector(d)
                    .OfClass(typeof(WireType)).Cast<WireType>().FirstOrDefault();
                var baseMaterial = d.Settings.ElectricalSetting?.WireMaterialTypes
                    ?.Cast<WireMaterialType>().FirstOrDefault();
                if (wireType is null || baseMaterial is null) return;

                var newMaterial = d.Settings.ElectricalSetting!.AddWireMaterialType(
                    "SC_TestMaterial", baseMaterial);
                var newWire = (WireType)wireType.Duplicate("SC_Wire");
                newWire.WireMaterial = newMaterial;
                seeded = true;
            });
            if (!seeded)
            {
                Skip.Test("В шаблоне нет WireType/WireMaterialType — сидирование невозможно");
                return;
            }

            var targetHasMaterial = false;
            targetTx.RunInTransaction(targetDoc, "Check target materials", d =>
            {
                targetHasMaterial = d.Settings.ElectricalSetting?.WireMaterialTypes
                    ?.Cast<WireMaterialType>()
                    .Any(m => m.Name == "SC_TestMaterial") == true;
            });
            await Assert.That(targetHasMaterial).IsFalse();

            var result = sync.SyncTypeFromSource(
                sourceDoc, targetDoc, "SC_Wire", "item-wire", "v1",
                int.Parse(Application.VersionNumber));
            await Assert.That(result.IsSuccess).IsTrue();

            var finder = new RevitSystemTypeFinder();
            var targetTypeId = finder.FindTypeByName(targetDoc, "SC_Wire", null);
            await Assert.That(targetTypeId).IsNotNull();

            var targetWire = (WireType)targetDoc.GetElement(targetTypeId!)!;
            await Assert.That(targetWire.WireMaterial?.Name).IsEqualTo("SC_TestMaterial");
        }
        finally
        {
            sourceDoc.Close(false);
            targetDoc.Close(false);
        }
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Sync_WireType_SameNameDistributionSystemInSource_BindsRealWireType()
    {
        // Manual test 2026-08-04 (round 3): the source mini-project contains
        // electrical settings ElementTypes (DistributionSysType etc.) sharing
        // the name and the degenerate 'Single' family key with the real
        // WireType. The category-scoped reference lookup must bind the
        // WireType — an unscoped match synced the distribution system's
        // parameters into the project instead.
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
            sourceTx.RunInTransaction(sourceDoc, "Seed shared-name source", d =>
            {
                var wireType = new FilteredElementCollector(d)
                    .OfClass(typeof(WireType)).Cast<WireType>().FirstOrDefault();
                var distributionSystem = new FilteredElementCollector(d)
                    .OfClass(typeof(DistributionSysType)).Cast<DistributionSysType>().FirstOrDefault();
                if (wireType is null || distributionSystem is null) return;

                var sharedWire = (WireType)wireType.Duplicate("SC_Shared");
                sharedWire.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)
                    ?.Set("real-wire");
                distributionSystem.Duplicate("SC_Shared");
                seeded = true;
            });
            if (!seeded)
            {
                Skip.Test("В шаблоне нет WireType/DistributionSysType — сидирование невозможно");
                return;
            }

            var result = sync.SyncTypeFromSource(
                sourceDoc, targetDoc, "SC_Shared", "item-wire", "v1",
                int.Parse(Application.VersionNumber),
                categoryOrdinal: (int)BuiltInCategory.OST_Wire);
            await Assert.That(result.IsSuccess).IsTrue();

            var finder = new RevitSystemTypeFinder();
            var targetTypeId = finder.FindTypeByName(
                targetDoc, "SC_Shared", (int)BuiltInCategory.OST_Wire);
            await Assert.That(targetTypeId).IsNotNull();

            var targetType = targetDoc.GetElement(targetTypeId!)!;
            using (Assert.Multiple())
            {
                await Assert.That(targetType is WireType).IsTrue();
                await Assert.That(
                    targetType.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)?.AsString())
                    .IsEqualTo("real-wire");
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
    public async Task Sync_WireType_RatingMissingInProject_WarnsAndCountsNotConverged()
    {
        // ADR-065 degradation path (audit C2): only the MATERIAL can be
        // created via the API — a temperature rating that does not exist
        // under the project's material cannot be created, so the member is
        // rejected: Warn in the log + NotConverged residue, but the sync
        // itself still succeeds (the remaining members apply).
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
            sourceTx.RunInTransaction(sourceDoc, "Seed wire with custom rating", d =>
            {
                var wireType = new FilteredElementCollector(d)
                    .OfClass(typeof(WireType)).Cast<WireType>().FirstOrDefault();
                var baseMaterial = d.Settings.ElectricalSetting?.WireMaterialTypes
                    ?.Cast<WireMaterialType>().FirstOrDefault();
                var baseRating = baseMaterial?.TemperatureRatings
                    ?.Cast<TemperatureRatingType>().FirstOrDefault();
                if (wireType is null || baseMaterial is null || baseRating is null) return;

                var material = d.Settings.ElectricalSetting!.AddWireMaterialType("SC_NC_Material", baseMaterial);
                var rating = material.AddTemperatureRatingType("SC_Rating_X", baseRating);
                var newWire = (WireType)wireType.Duplicate("SC_Wire_NC");
                newWire.WireMaterial = material;
                newWire.TemperatureRating = rating;
                seeded = true;
            });
            if (!seeded)
            {
                Skip.Test("В шаблоне нет WireType/WireMaterialType — сидирование невозможно");
                return;
            }

            var result = sync.SyncTypeFromSource(
                sourceDoc, targetDoc, "SC_Wire_NC", "item-wire", "v1",
                int.Parse(Application.VersionNumber));

            using (Assert.Multiple())
            {
                await Assert.That(result.IsSuccess).IsTrue();
                // The material is created+assigned, the rating is rejected
                // (and everything under it) — the residue must be counted,
                // never silently skipped.
                await Assert.That(result.NotConvergedCount).IsGreaterThan(0);
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
