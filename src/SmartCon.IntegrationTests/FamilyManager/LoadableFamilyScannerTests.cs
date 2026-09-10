using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// Issue #196: <see cref="LoadableFamilyScanner"/> must exclude non-editable
/// families (curtain wall system panels, «Системная панель»,
/// OST_CurtainWallPanels) — <c>Document.EditFamily</c> throws
/// ArgumentException for them, so the loadable batch import would always
/// fail on such a row (ADR-027 §"Not supported").
/// </summary>
public sealed class LoadableFamilyScannerTests : RevitApiTest
{
    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task GetUniqueFamilies_CurtainWallPlaced_ExcludesNonEditableSystemPanel()
    {
        var doc = SampleFiles.NewMepTemplateDocument(Application);
        if (doc is null) { Skip.Test("Нет MEP-шаблона"); return; }
        try
        {
            var txService = new RevitTransactionService(new StubRevitContext(doc));
            string? panelFamilyUniqueId = null;
            var seeded = false;

            txService.RunInTransaction(doc, "Seed curtain wall", d =>
            {
                var curtainWallType = new FilteredElementCollector(d)
                    .OfClass(typeof(WallType)).Cast<WallType>()
                    .FirstOrDefault(t => t.Kind == WallKind.Curtain);
                if (curtainWallType is null) return;

                var level = new FilteredElementCollector(d)
                    .OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).First();

                var line = Line.CreateBound(XYZ.Zero, new XYZ(19.685, 0, 0));
                Wall.Create(d, line, curtainWallType.Id, level.Id, 9.8425, 0, false, false);

                var panelFamily = new FilteredElementCollector(d)
                    .OfClass(typeof(FamilyInstance))
                    .WhereElementIsNotElementType()
                    .Cast<FamilyInstance>()
                    .Select(fi => fi.Symbol?.Family)
                    .FirstOrDefault(f => f is not null && !f.IsEditable);
                if (panelFamily is null) return;

                panelFamilyUniqueId = panelFamily.UniqueId;
                seeded = true;
            });

            if (!seeded || panelFamilyUniqueId is null)
            {
                Skip.Test("В шаблоне нет витражного типа стены либо панели не материализовались");
                return;
            }

            var scanner = new LoadableFamilyScanner();
            var families = scanner.GetUniqueFamilies(doc);

            await Assert.That(families.Any(f => f.FamilyUniqueId == panelFamilyUniqueId)).IsFalse();
        }
        finally { doc.Close(false); }
    }
}
