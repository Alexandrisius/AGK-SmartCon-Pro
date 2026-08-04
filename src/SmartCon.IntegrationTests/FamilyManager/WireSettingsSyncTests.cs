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

    private sealed class NullFittingDependencyResolver : IFittingDependencyResolver
    {
        public ElementId? EnsureFitting(
            Document activeDoc, string familyName, string typeName, int targetRevitVersion) => null;
    }
}
