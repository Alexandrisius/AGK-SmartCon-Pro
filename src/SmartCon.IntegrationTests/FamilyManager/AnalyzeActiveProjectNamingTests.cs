using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Services.Interfaces;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// Issue #192 (naming): <c>AnalyzeActiveProject</c> must take the category
/// display name from the DOCUMENT (the same source as the picker and the
/// snapshot extractor), not from the registry fallback. Display names of
/// built-in categories differ per template/version/locale — the document
/// is the single source of truth.
/// </summary>
public sealed class AnalyzeActiveProjectNamingTests : RevitApiTest
{
    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task AnalyzeActiveProject_CategoryDisplayName_ComesFromDocument()
    {
        var doc = SampleFiles.NewMepTemplateDocument(Application);
        if (doc is null) { Skip.Test("Нет MEP-шаблона с типами изоляции (CF-4720)"); }
        try
        {
            var txService = new RevitTransactionService(new StubRevitContext(doc));
            SeedInsulatedPipe(doc, txService);

            var ops = new SystemFamilyRevitOperations(
                null!,
                txService,
                new LoadableFamilyScanner(),
                new RevitMiniProjectMarker(txService, new SystemClock()));
            var analyses = ops.AnalyzeActiveProject(doc);

            var insulation = analyses.FirstOrDefault(
                a => a.Category == BuiltInCategory.OST_PipeInsulations);
            await Assert.That(insulation).IsNotNull();

            // Контракт: DisplayName == имя категории в документе (локаль-
            // агностично). В RU-Revit это «Материалы изоляции трубопроводов»,
            // а НЕ registry fallback «Изоляция труб».
            var documentName = Category.GetCategory(doc, BuiltInCategory.OST_PipeInsulations)?.Name;
            await Assert.That(documentName).IsNotNull();
            await Assert.That(insulation!.DisplayName).IsEqualTo(documentName!);
        }
        finally { doc.Close(false); }
    }

    private static void SeedInsulatedPipe(Document doc, RevitTransactionService txService)
    {
        txService.RunInTransaction(doc, "Seed insulated pipe", d =>
        {
            var pipeType = new FilteredElementCollector(d)
                .OfClass(typeof(PipeType)).Cast<PipeType>().FirstOrDefault()
                ?? throw new InvalidOperationException("В шаблоне нет PipeType");
            var systemType = new FilteredElementCollector(d)
                .OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>().FirstOrDefault()
                ?? throw new InvalidOperationException("В шаблоне нет PipingSystemType");
            var level = new FilteredElementCollector(d)
                .OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).First();
            var insulationType = new FilteredElementCollector(d)
                .OfClass(typeof(PipeInsulationType)).Cast<PipeInsulationType>().FirstOrDefault()
                ?? throw new InvalidOperationException("В шаблоне нет PipeInsulationType (CF-4720)");

            var pipe = Pipe.Create(d, systemType.Id, pipeType.Id, level.Id,
                XYZ.Zero, new XYZ(3.280839895, 0, 0));
            PipeInsulation.Create(d, pipe.Id, insulationType.Id, 0.082);
        });
    }
}
