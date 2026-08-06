using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Services.Interfaces;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Compatibility;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// Issue #197: OST_DuctLinings (внутренняя изоляция/футеровка воздуховодов) —
/// 15-я системная категория реестра. <c>DuctLining.Create</c> host-required
/// (как у наружной изоляции); <c>DuctLining : InsulationLiningBase</c>, поэтому
/// lining-хосты исключаются существующим <c>InsulationHostFilter</c>.
/// </summary>
public sealed class DuctLiningPlacementTests : RevitApiTest
{
    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task DuctLining_Placed_InstanceHasTargetType()
    {
        var doc = SampleFiles.NewMepTemplateDocument(Application);
        if (doc is null) { Skip.Test("Нет MEP-шаблона"); return; }
        try
        {
            var liningType = new FilteredElementCollector(doc)
                .OfClass(typeof(DuctLiningType)).Cast<DuctLiningType>().FirstOrDefault();
            if (liningType is null) { Skip.Test("В шаблоне нет DuctLiningType"); return; }

            var revitMajor = int.Parse(Application.VersionNumber);
            var handler = SystemCategoryRegistry.GetPlacementHandler(BuiltInCategory.OST_DuctLinings, revitMajor);
            if (handler is null) { Skip.Test("Нет handler"); return; }

            var level = new FilteredElementCollector(doc)
                .OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).First();

            var txService = new RevitTransactionService(new StubRevitContext(doc));
            var start = XYZ.Zero;
            var end = new XYZ(RevitUnitsCompat.MetersToInternal(1.0), 0, level.Elevation);
            var created = handler!(doc, txService, liningType, level, start, end);

            using (Assert.Multiple())
            {
                await Assert.That(created).IsNotNull();
                await Assert.That(created).IsTypeOf<DuctLining>();
                await Assert.That(created!.GetTypeId()).IsEqualTo(liningType.Id);
            }
        }
        finally { doc.Close(false); }
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task AnalyzeActiveProject_LiningCategoryDetected_HostDuctExcluded()
    {
        var doc = SampleFiles.NewMepTemplateDocument(Application);
        if (doc is null) { Skip.Test("Нет MEP-шаблона"); return; }
        try
        {
            var liningType = new FilteredElementCollector(doc)
                .OfClass(typeof(DuctLiningType)).Cast<DuctLiningType>().FirstOrDefault();
            if (liningType is null) { Skip.Test("В шаблоне нет DuctLiningType"); return; }

            var txService = new RevitTransactionService(new StubRevitContext(doc));
            ElementId? hostDuctTypeId = null;
            txService.RunInTransaction(doc, "Seed lined duct", d =>
            {
                var sysType = new FilteredElementCollector(d)
                    .OfClass(typeof(MechanicalSystemType)).Cast<MechanicalSystemType>().First();
                var ductType = new FilteredElementCollector(d)
                    .OfClass(typeof(DuctType)).Cast<DuctType>().First();
                hostDuctTypeId = ductType.Id;
                var level = new FilteredElementCollector(d)
                    .OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).First();
                var duct = Duct.Create(d, sysType.Id, ductType.Id, level.Id,
                    XYZ.Zero, new XYZ(3.280839895, 0, 0));
                DuctLining.Create(d, duct.Id, liningType.Id, 0.082);
            });

            // #181 scope correction (2026-08-06): the host exclusion applies
            // ONLY to SmartCon mini-projects — mark the document so this
            // test exercises the exclusion path (unmarked projects keep
            // host ducts as regular content, see InsulationHostExclusionTests).
            new RevitMiniProjectMarker(txService, new SystemClock())
                .MarkAsMiniProject(doc, catalogItemId: null);

            var ops = new SystemFamilyRevitOperations(
                null!,
                txService,
                new LoadableFamilyScanner(),
                new RevitMiniProjectMarker(txService, new SystemClock()));
            var analyses = ops.AnalyzeActiveProject(doc);

            // Lining category detected with the lining type and the
            // document's category display name (#192 contract).
            var lining = analyses.FirstOrDefault(a => a.Category == BuiltInCategory.OST_DuctLinings);
            await Assert.That(lining).IsNotNull();
            await Assert.That(lining!.Types.Any(t => t.Name == liningType.Name)).IsTrue();
            var documentName = Category.GetCategory(doc, BuiltInCategory.OST_DuctLinings)?.Name;
            if (documentName is not null)
            {
                await Assert.That(lining.DisplayName).IsEqualTo(documentName);
            }

            // The host duct exists ONLY as a lining host → its type must not
            // appear in OST_DuctCurves (InsulationHostFilter covers lining
            // via InsulationLiningBase).
            var ducts = analyses.FirstOrDefault(a => a.Category == BuiltInCategory.OST_DuctCurves);
            if (ducts is not null && hostDuctTypeId is not null)
            {
                var hostTypeName = doc.GetElement(hostDuctTypeId)!.Name;
                await Assert.That(ducts.Types.Any(t => t.Name == hostTypeName)).IsFalse();
            }
        }
        finally { doc.Close(false); }
    }
}
