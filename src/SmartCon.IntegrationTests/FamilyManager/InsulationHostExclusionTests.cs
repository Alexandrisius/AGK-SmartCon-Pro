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
/// Issue #181 (scope corrected 2026-08-06): the insulation-host exclusion in
/// <c>AnalyzeActiveProject</c> applies ONLY to SmartCon mini-projects (ES
/// marker, ADR-062) — their host pipes/ducts exist solely to stage insulation
/// types (CF-4720). In a regular working project an insulated pipe/duct is
/// real user content and is offered for import. The picker
/// (<c>PickSelectedElements</c>) never applies the filter.
/// </summary>
public sealed class InsulationHostExclusionTests : RevitApiTest
{
    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task AnalyzeActiveProject_RegularProject_InsulatedAndBarePipesBothKept()
    {
        var doc = SampleFiles.NewMepTemplateDocument(Application);
        if (doc is null) { Skip.Test("Нет MEP-шаблона с типами изоляции (CF-4720)"); }
        try
        {
            var txService = new RevitTransactionService(new StubRevitContext(doc));
            var (bareTypeName, insulatedTypeName) = SeedTwoPipesOneInsulated(doc, txService);

            // Документ НЕ помечен маркером мини-проекта — обычный рабочий проект.
            var ops = CreateOperations(doc, txService);
            var analyses = ops.AnalyzeActiveProject(doc);

            var pipeCategory = analyses.FirstOrDefault(a => a.Category == BuiltInCategory.OST_PipeCurves);
            using (Assert.Multiple())
            {
                await Assert.That(pipeCategory).IsNotNull();
                var typeNames = pipeCategory!.Types.Select(t => t.Name).ToList();
                await Assert.That(typeNames).Contains(bareTypeName);
                await Assert.That(typeNames).Contains(insulatedTypeName);
            }
        }
        finally { doc.Close(false); }
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task AnalyzeActiveProject_MarkedMiniProject_InsulatedExcluded_BareKept()
    {
        var doc = SampleFiles.NewMepTemplateDocument(Application);
        if (doc is null) { Skip.Test("Нет MEP-шаблона с типами изоляции (CF-4720)"); }
        try
        {
            var txService = new RevitTransactionService(new StubRevitContext(doc));
            var (bareTypeName, insulatedTypeName) = SeedTwoPipesOneInsulated(doc, txService);

            var marker = new RevitMiniProjectMarker(txService, new SystemClock());
            marker.MarkAsMiniProject(doc, catalogItemId: null);

            var ops = CreateOperations(doc, txService);
            var analyses = ops.AnalyzeActiveProject(doc);

            var pipeCategory = analyses.FirstOrDefault(a => a.Category == BuiltInCategory.OST_PipeCurves);
            using (Assert.Multiple())
            {
                await Assert.That(pipeCategory).IsNotNull();
                var typeNames = pipeCategory!.Types.Select(t => t.Name).ToList();
                await Assert.That(typeNames).Contains(bareTypeName);
                await Assert.That(typeNames).DoesNotContain(insulatedTypeName);
            }
        }
        finally { doc.Close(false); }
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task AnalyzeActiveProject_MarkedMiniProject_AllPipesInsulated_PipeTypeAbsent()
    {
        var doc = SampleFiles.NewMepTemplateDocument(Application);
        if (doc is null) { Skip.Test("Нет MEP-шаблона с типами изоляции (CF-4720)"); }
        try
        {
            var txService = new RevitTransactionService(new StubRevitContext(doc));
            SeedSingleInsulatedPipe(doc, txService);

            var marker = new RevitMiniProjectMarker(txService, new SystemClock());
            marker.MarkAsMiniProject(doc, catalogItemId: null);

            var ops = CreateOperations(doc, txService);
            var analyses = ops.AnalyzeActiveProject(doc);

            // В мини-проекте тип изолированной трубы не должен попасть в
            // анализ (категория в целом может отсутствовать — оба исхода
            // легальны, проверка строго по типу).
            var pipeCategory = analyses.FirstOrDefault(a => a.Category == BuiltInCategory.OST_PipeCurves);
            if (pipeCategory is not null)
            {
                var typeNames = pipeCategory.Types.Select(t => t.Name).ToList();
                await Assert.That(typeNames).DoesNotContain(InsulatedOnlyTypeName);
            }
        }
        finally { doc.Close(false); }
    }

    private const string BareTypeName = "SC_BarePipe";
    private const string InsulatedTypeName = "SC_InsulatedPipe";
    private const string InsulatedOnlyTypeName = "SC_InsulatedOnly";

    private static (string Bare, string Insulated) SeedTwoPipesOneInsulated(
        Document doc, RevitTransactionService txService)
    {
        txService.RunInTransaction(doc, "Seed two pipes", d =>
        {
            var (pipeType, systemType, level, insulationType) = FindMepSeeds(d);
            var bareType = pipeType.Duplicate(BareTypeName) as PipeType;
            var insulatedType = pipeType.Duplicate(InsulatedTypeName) as PipeType;

            var barePipe = Pipe.Create(d, systemType.Id, bareType!.Id, level.Id,
                XYZ.Zero, new XYZ(3.280839895, 0, 0));
            var insulatedPipe = Pipe.Create(d, systemType.Id, insulatedType!.Id, level.Id,
                new XYZ(0, 3.280839895, 0), new XYZ(3.280839895, 3.280839895, 0));
            PipeInsulation.Create(d, insulatedPipe.Id, insulationType.Id, 0.082);
        });
        return (BareTypeName, InsulatedTypeName);
    }

    private static void SeedSingleInsulatedPipe(Document doc, RevitTransactionService txService)
    {
        txService.RunInTransaction(doc, "Seed insulated pipe", d =>
        {
            var (pipeType, systemType, level, insulationType) = FindMepSeeds(d);
            var insulatedType = pipeType.Duplicate(InsulatedOnlyTypeName) as PipeType;

            var insulatedPipe = Pipe.Create(d, systemType.Id, insulatedType!.Id, level.Id,
                XYZ.Zero, new XYZ(3.280839895, 0, 0));
            PipeInsulation.Create(d, insulatedPipe.Id, insulationType.Id, 0.082);
        });
    }

    private static (PipeType PipeType, PipingSystemType SystemType, Level Level, PipeInsulationType InsulationType)
        FindMepSeeds(Document doc)
    {
        var pipeType = new FilteredElementCollector(doc)
            .OfClass(typeof(PipeType)).Cast<PipeType>().FirstOrDefault()
            ?? throw new InvalidOperationException("В шаблоне нет PipeType");
        var systemType = new FilteredElementCollector(doc)
            .OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>().FirstOrDefault()
            ?? throw new InvalidOperationException("В шаблоне нет PipingSystemType");
        var level = new FilteredElementCollector(doc)
            .OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).First();
        var insulationType = new FilteredElementCollector(doc)
            .OfClass(typeof(PipeInsulationType)).Cast<PipeInsulationType>().FirstOrDefault()
            ?? throw new InvalidOperationException("В шаблоне нет PipeInsulationType (CF-4720)");
        return (pipeType, systemType, level, insulationType);
    }

    private static SystemFamilyRevitOperations CreateOperations(
        Document doc, RevitTransactionService txService)
    {
        // AnalyzeActiveProject не использует IRevitUIContext (нужен только
        // PickSelectedElements для picking) — в headless-хосте UIDocument
        // недоступен (RevitAPIUI-типы крашат сессию, правило 5), поэтому
        // передаём null!: тестируемый метод его не коснётся.
        return new SystemFamilyRevitOperations(
            null!,
            txService,
            new LoadableFamilyScanner(),
            new RevitMiniProjectMarker(txService, new SystemClock()),
            TestSystemTypeSyncServiceFactory.Create(doc));
    }
}
