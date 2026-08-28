using Autodesk.Revit.DB;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Support;

/// <summary>
/// Wires a <see cref="SystemTypeSyncService"/> for tests that construct
/// <see cref="SystemFamilyRevitOperations"/> directly (its staging path
/// delegates MEPCurve types to the sync service, ADR-072). Fittings are
/// resolved by a no-op resolver — tests stage into template projects
/// without catalog fittings.
/// </summary>
internal static class TestSystemTypeSyncServiceFactory
{
    public static SystemTypeSyncService Create(Document targetDoc)
    {
        var materialSync = new RevitMaterialSyncService();
        return new SystemTypeSyncService(
            new RevitTransactionService(new StubRevitContext(targetDoc)),
            new RevitFamilySnapshotExtractor(),
            new RevitSystemTypeFinder(),
            new SystemClock(),
            materialSync,
            new RevitSegmentSyncService(materialSync),
            new NullFittingResolver(),
            new RevitCompoundStructureSyncService(materialSync));
    }

    private sealed class NullFittingResolver : IFittingDependencyResolver
    {
        public ElementId? EnsureFitting(
            Document activeDoc, string familyName, string typeName, int targetRevitVersion, string? parentCatalogItemId = null) => null;
    }
}
