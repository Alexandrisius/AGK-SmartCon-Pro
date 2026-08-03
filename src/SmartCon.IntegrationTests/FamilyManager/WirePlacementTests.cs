using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Compatibility;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// Issue #199: OST_Wire (провода) — 16-я системная категория реестра.
/// <c>Wire.Create</c> требует viewId плана этажа/RCP — handler
/// переиспользует существующий план уровня или создаёт его через
/// <c>ViewPlan.Create</c>.
/// </summary>
public sealed class WirePlacementTests : RevitApiTest
{
    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Wire_Placed_InstanceHasTargetType()
    {
        var doc = SampleFiles.NewMepTemplateDocument(Application);
        if (doc is null) { Skip.Test("Нет MEP-шаблона"); return; }
        try
        {
            var wireType = new FilteredElementCollector(doc)
                .OfClass(typeof(WireType)).Cast<WireType>().FirstOrDefault();
            if (wireType is null) { Skip.Test("В шаблоне нет WireType"); return; }

            var revitMajor = int.Parse(Application.VersionNumber);
            var handler = SystemCategoryRegistry.GetPlacementHandler(BuiltInCategory.OST_Wire, revitMajor);
            if (handler is null) { Skip.Test("Нет handler"); return; }

            var level = new FilteredElementCollector(doc)
                .OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).First();

            var txService = new RevitTransactionService(new StubRevitContext(doc));
            var start = XYZ.Zero;
            var end = new XYZ(RevitUnitsCompat.MetersToInternal(1.0), 0, level.Elevation);
            var created = handler!(doc, txService, wireType, level, start, end);

            using (Assert.Multiple())
            {
                await Assert.That(created).IsNotNull();
                await Assert.That(created).IsTypeOf<Wire>();
                await Assert.That(created!.GetTypeId()).IsEqualTo(wireType.Id);
            }
        }
        finally { doc.Close(false); }
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Wire_Placed_FloorPlanViewExists()
    {
        // The handler must leave a valid plan view behind (existing or
        // created) — the wire is a view-specific element.
        var doc = SampleFiles.NewMepTemplateDocument(Application);
        if (doc is null) { Skip.Test("Нет MEP-шаблона"); return; }
        try
        {
            var wireType = new FilteredElementCollector(doc)
                .OfClass(typeof(WireType)).Cast<WireType>().FirstOrDefault();
            if (wireType is null) { Skip.Test("В шаблоне нет WireType"); return; }

            var revitMajor = int.Parse(Application.VersionNumber);
            var handler = SystemCategoryRegistry.GetPlacementHandler(BuiltInCategory.OST_Wire, revitMajor);
            if (handler is null) { Skip.Test("Нет handler"); return; }

            var level = new FilteredElementCollector(doc)
                .OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).First();

            var txService = new RevitTransactionService(new StubRevitContext(doc));
            var created = handler!(doc, txService, wireType, level,
                XYZ.Zero, new XYZ(RevitUnitsCompat.MetersToInternal(1.0), 0, level.Elevation));
            // null here means the handler degraded (no FloorPlan view family
            // type in the template) — a real defect, not an environmental skip.
            await Assert.That(created).IsNotNull();

            var wire = (Wire)created!;
            var view = (ViewPlan)doc.GetElement(wire.OwnerViewId)!;
            await Assert.That(
                view.ViewType == ViewType.FloorPlan || view.ViewType == ViewType.CeilingPlan).IsTrue();
        }
        finally { doc.Close(false); }
    }
}
