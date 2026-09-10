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
/// <see cref="WireType"/> API properties — they never appear in
/// Element.Parameters, so the generic parameter pipeline cannot sync them.
/// On Revit ≤2025 they are backed by the ElectricalSetting object graph: a
/// material missing in the project is created by duplicating an existing
/// one, missing rating/insulation/size cannot be created (Warn +
/// NotConverged). On Revit 2026+ (#233) the flat Conductor* model allows
/// creating EVERY conductor kind — the sync fully converges by name, and a
/// created conductor size carries the source diameter.
/// </summary>
public sealed class WireSettingsSyncTests : RevitApiTest
{
#if REVIT2026_OR_GREATER
    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Sync_WireType_MaterialChangeInReference_CreatedAndAssignedInTarget()
    {
        // #233 (R26 variant): the source material is a ConductorMaterial of
        // the flat model — the target project lacks it, so the sync must
        // CREATE it by name (ConductorMaterial.Create + Name) and assign its
        // id to the target wire type.
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
                if (wireType is null) return;

                var material = ConductorMaterial.Create(d);
                material.Name = "SC_TestMaterial";
                var newWire = (WireType)wireType.Duplicate("SC_Wire");
                newWire.WireMaterial = material.Id;
                seeded = true;
            });
            if (!seeded)
            {
                Skip.Test("В шаблоне нет WireType — сидирование невозможно");
                return;
            }

            var targetHasMaterial = false;
            targetTx.RunInTransaction(targetDoc, "Check target materials", d =>
            {
                try
                {
                    var id = ConductorMaterial.GetConductorMaterialIdByName(d, "SC_TestMaterial");
                    targetHasMaterial = id is not null && id != ElementId.InvalidElementId;
                }
                catch (Exception)
                {
                    // not-found behaviour of the by-name static is undocumented
                    targetHasMaterial = false;
                }
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
            using var targetMaterial = ConductorMaterial.GetConductorMaterial(targetDoc, targetWire.WireMaterial);
            await Assert.That(targetMaterial.Name).IsEqualTo("SC_TestMaterial");
        }
        finally
        {
            sourceDoc.Close(false);
            targetDoc.Close(false);
        }
    }
#else
    // Revit 2026 replaced the ElectricalSetting object graph with the
    // Conductor* model — the ≤2025 seeding/assertion API (WireMaterialTypes,
    // AddWireMaterialType, TemperatureRatings) does not compile against the
    // 2026 API; the R26 variant above seeds through ConductorMaterial.
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
#endif

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

#if REVIT2026_OR_GREATER
    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Sync_WireType_RatingMissingInProject_CreatedAndAssigned_FlatConductorModel()
    {
        // #233 (R26 variant of the ≤2025 degradation test): the old
        // hierarchy could NOT create a temperature rating (Warn +
        // NotConverged residue). The 2026 flat Conductor* model creates any
        // missing conductor object by name — the sync must FULLY converge:
        // material and rating are created in the target and assigned.
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
                if (wireType is null) return;

                var material = ConductorMaterial.Create(d);
                material.Name = "SC_NC_Material";
                var rating = TemperatureRating.Create(d);
                rating.Name = "SC_Rating_X";
                var newWire = (WireType)wireType.Duplicate("SC_Wire_NC");
                newWire.WireMaterial = material.Id;
                newWire.TemperatureRating = rating.Id;
                seeded = true;
            });
            if (!seeded)
            {
                Skip.Test("В шаблоне нет WireType — сидирование невозможно");
                return;
            }

            var result = sync.SyncTypeFromSource(
                sourceDoc, targetDoc, "SC_Wire_NC", "item-wire", "v1",
                int.Parse(Application.VersionNumber));

            var finder = new RevitSystemTypeFinder();
            var targetTypeId = finder.FindTypeByName(targetDoc, "SC_Wire_NC", null);
            await Assert.That(targetTypeId).IsNotNull();
            var targetWire = (WireType)targetDoc.GetElement(targetTypeId!)!;
            using var targetRating = TemperatureRating.GetTemperatureRating(targetDoc, targetWire.TemperatureRating);

            using (Assert.Multiple())
            {
                await Assert.That(result.IsSuccess).IsTrue();
                // Flat model: nothing may remain unconverged — the rating is
                // created by name, unlike the ≤2025 degradation path.
                await Assert.That(result.NotConvergedCount).IsEqualTo(0);
                await Assert.That(targetRating.Name).IsEqualTo("SC_Rating_X");
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
    public async Task Sync_WireType_MaxSizeMissingInProject_CreatedWithSourceDiameter()
    {
        // #233: WireType.MaxSize accepts only an EXISTING conductor-size
        // name — the sync must create the missing size in the target and
        // carry its numeric content (diameter, internal units) from the
        // source document, because the snapshot/hash holds names only.
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

            const double seededDiameter = 0.0312; // ~9.5 mm in internal feet
            var seeded = false;
            sourceTx.RunInTransaction(sourceDoc, "Seed wire with custom size", d =>
            {
                var wireType = new FilteredElementCollector(d)
                    .OfClass(typeof(WireType)).Cast<WireType>().FirstOrDefault();
                if (wireType is null) return;

                var size = ConductorSize.Create(d);
                size.Name = "SC_Size_95";
                size.Diameter = seededDiameter;
                var newWire = (WireType)wireType.Duplicate("SC_Wire_SZ");
                newWire.MaxSize = "SC_Size_95";
                seeded = true;
            });
            if (!seeded)
            {
                Skip.Test("В шаблоне нет WireType — сидирование невозможно");
                return;
            }

            var result = sync.SyncTypeFromSource(
                sourceDoc, targetDoc, "SC_Wire_SZ", "item-wire", "v1",
                int.Parse(Application.VersionNumber));

            var finder = new RevitSystemTypeFinder();
            var targetTypeId = finder.FindTypeByName(targetDoc, "SC_Wire_SZ", null);
            await Assert.That(targetTypeId).IsNotNull();
            var targetWire = (WireType)targetDoc.GetElement(targetTypeId!)!;

            ElementId? sizeId = null;
            var sizeDiameter = -1.0;
            targetTx.RunInTransaction(targetDoc, "Read created size", d =>
            {
                try
                {
                    sizeId = ConductorSize.GetConductorSizeIdByName(d, "SC_Size_95");
                    if (sizeId is not null && sizeId != ElementId.InvalidElementId)
                    {
                        using var size = ConductorSize.GetConductorSize(d, sizeId);
                        sizeDiameter = size.Diameter;
                    }
                }
                catch (Exception)
                {
                    sizeId = ElementId.InvalidElementId;
                }
            });

            using (Assert.Multiple())
            {
                await Assert.That(result.IsSuccess).IsTrue();
                await Assert.That(sizeId).IsNotNull();
                await Assert.That(sizeId != ElementId.InvalidElementId).IsTrue();
                await Assert.That(targetWire.MaxSize).IsEqualTo("SC_Size_95");
                await Assert.That(Math.Abs(sizeDiameter - seededDiameter) < 1e-9).IsTrue();
            }
        }
        finally
        {
            sourceDoc.Close(false);
            targetDoc.Close(false);
        }
    }
#else
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
#endif

    private sealed class NullFittingDependencyResolver : IFittingDependencyResolver
    {
        public ElementId? EnsureFitting(
            Document activeDoc, string familyName, string typeName, int targetRevitVersion, string? parentCatalogItemId = null) => null;
    }
}
