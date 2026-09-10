using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Services.Interfaces;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// MiniProjectViewConfigurator (Issue #205): презентационный вид мини-проекта,
/// запекаемый при staging до SaveAs. Покрытие: 3D-вид получает Fine +
/// ShadedWithEdges и назначается стартовым; провода стартуют на плане этажа;
/// недостающий 3D-вид создаётся; настройки переживают SaveAs+reopen.
/// </summary>
public sealed class MiniProjectViewConfiguratorTests : RevitApiTest
{
    private Document? _document;
    private RevitTransactionService? _transactions;
    private string? _tempPathToCleanup;

    private Document Doc => _document!;
    private RevitTransactionService Transactions => _transactions!;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CreateDocument()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CloseDocument()
    {
        _document?.Close(false);
        _document = null;
        if (_tempPathToCleanup is not null && File.Exists(_tempPathToCleanup))
        {
            File.Delete(_tempPathToCleanup);
            _tempPathToCleanup = null;
        }
    }

    [Test]
    public async Task Configure_NonWireCategory_3DViewFineShadedWithEdgesStartingView()
    {
        MiniProjectViewConfigurator.Configure(Transactions, Doc, BuiltInCategory.OST_PipeCurves);

        var view3d = CollectFirst3DView(Doc);
        await Assert.That(view3d).IsNotNull();
        await Assert.That(view3d!.DetailLevel).IsEqualTo(ViewDetailLevel.Fine);
        await Assert.That(view3d.DisplayStyle).IsEqualTo(DisplayStyle.ShadingWithEdges);

        var settings = StartingViewSettings.GetStartingViewSettings(Doc);
        await Assert.That(settings).IsNotNull();
        await Assert.That(settings!.ViewId).IsEqualTo(view3d.Id);
    }

    [Test]
    public async Task Configure_WireCategory_StartingViewIsFloorPlan()
    {
        // Wires are plan-view-only (Wire.Create), so the wire mini starts on
        // the floor plan; the 3D view still gets the presentation settings.
        MiniProjectViewConfigurator.Configure(Transactions, Doc, BuiltInCategory.OST_Wire);

        var settings = StartingViewSettings.GetStartingViewSettings(Doc);
        await Assert.That(settings).IsNotNull();
        var startingView = Doc.GetElement(settings!.ViewId) as View;

        await Assert.That(startingView).IsNotNull();
        await Assert.That(startingView!.ViewType).IsEqualTo(ViewType.FloorPlan);
        await Assert.That(startingView.DetailLevel).IsEqualTo(ViewDetailLevel.Fine);
        await Assert.That(startingView.DisplayStyle).IsEqualTo(DisplayStyle.ShadingWithEdges);

        var view3d = CollectFirst3DView(Doc);
        await Assert.That(view3d).IsNotNull();
        await Assert.That(view3d!.DetailLevel).IsEqualTo(ViewDetailLevel.Fine);
        await Assert.That(view3d.DisplayStyle).IsEqualTo(DisplayStyle.ShadingWithEdges);
    }

    [Test]
    public async Task Configure_TemplateWithout3DView_CreatesItAndSetsAsStartingView()
    {
        // Template machines differ — if a 3D view or its ViewFamilyType is
        // absent, the create-branch is not exercisable here.
        var existing3d = CollectFirst3DView(Doc);
        if (existing3d is not null)
        {
            var committed = Transactions.RunInTransaction(Doc, "Delete 3D views", doc =>
            {
                foreach (var id in Collect3DViewIds(doc).ToList())
                    doc.Delete(id);
            });
            if (!committed || CollectFirst3DView(Doc) is not null)
            {
                Skip.Test("Template keeps the last 3D view undeletable");
                return;
            }
        }

        MiniProjectViewConfigurator.Configure(Transactions, Doc, BuiltInCategory.OST_PipeCurves);

        var view3d = CollectFirst3DView(Doc);
        await Assert.That(view3d).IsNotNull();
        await Assert.That(view3d!.Name).IsEqualTo("SmartCon 3D");

        var settings = StartingViewSettings.GetStartingViewSettings(Doc);
        await Assert.That(settings).IsNotNull();
        await Assert.That(settings!.ViewId).IsEqualTo(view3d.Id);
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Configure_SettingsSurviveSaveAsAndReopen()
    {
        // The staging pipeline SaveAs-es the background document right after
        // Configure — the baked settings must travel inside the saved file.
#pragma warning disable TUnit0018 // reopen flow requires swapping the fixture document
        _tempPathToCleanup = Path.Combine(Path.GetTempPath(), $"smartcon-mkv-{Guid.NewGuid().ToString("N")}.rvt");

        MiniProjectViewConfigurator.Configure(Transactions, Doc, BuiltInCategory.OST_PipeCurves);
        Doc.SaveAs(_tempPathToCleanup, new SaveAsOptions { OverwriteExistingFile = true });
        Doc.Close(false);

        _document = Application.OpenDocumentFile(_tempPathToCleanup);
#pragma warning restore TUnit0018

        var settings = StartingViewSettings.GetStartingViewSettings(Doc);
        await Assert.That(settings).IsNotNull();
        var startingView = Doc.GetElement(settings!.ViewId) as View3D;

        await Assert.That(startingView).IsNotNull();
        await Assert.That(startingView!.DetailLevel).IsEqualTo(ViewDetailLevel.Fine);
        await Assert.That(startingView.DisplayStyle).IsEqualTo(DisplayStyle.ShadingWithEdges);
    }

    private static View3D? CollectFirst3DView(Document doc) => new FilteredElementCollector(doc)
        .OfClass(typeof(View3D))
        .Cast<View3D>()
        .Where(v => !v.IsTemplate)
        .OrderBy(v => v.Id.GetValue())
        .FirstOrDefault();

    private static IEnumerable<ElementId> Collect3DViewIds(Document doc) => new FilteredElementCollector(doc)
        .OfClass(typeof(View3D))
        .Cast<View3D>()
        .Where(v => !v.IsTemplate)
        .Select(v => v.Id);
}
