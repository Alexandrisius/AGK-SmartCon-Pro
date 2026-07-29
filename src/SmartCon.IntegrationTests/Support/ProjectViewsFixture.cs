using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.Support;

/// <summary>
/// Базовая фикстура для тестов Share Project: in-memory документ с уровнем,
/// тремя планами этажей ("SC_Alpha", "SC_Beta", шаблонный "SC_Templ")
/// и одной ведомостью ("SC_Schedule"). Имена с префиксом SC_ не пересекаются
/// с видами дефолтного шаблона.
/// </summary>
public abstract class ProjectViewsFixture : RevitApiTest
{
    protected const string KeepViewName = "SC_Alpha";
    protected const string DeleteViewName = "SC_Beta";
    protected const string TemplateViewName = "SC_Templ";
    protected const string ScheduleName = "SC_Schedule";

    private Document? _document;
    private RevitTransactionService? _transactions;

    protected Document Doc => _document!;
    protected RevitTransactionService Transactions => _transactions!;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void SeedViews()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasFloorPlanViewType(Doc))
        {
            Skip.Test("В шаблоне проекта по умолчанию нет ViewFamilyType для плана этажа — сидирование видов невозможно");
        }

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));

        Transactions.RunInTransaction(Doc, "Seed views", doc =>
        {
            var level = ModelSeed.CreateLevel(doc);
            _ = ModelSeed.CreateViewPlanChecked(doc, level.Id, KeepViewName);
            _ = ModelSeed.CreateViewPlanChecked(doc, level.Id, DeleteViewName);

            // View.IsTemplate read-only: шаблон создаётся только из существующего вида
            var templateSource = ModelSeed.CreateViewPlanChecked(doc, level.Id, "SC_TemplateSource");
            var template = templateSource.CreateViewTemplate();
            template.Name = TemplateViewName;

            var schedule = ModelSeed.CreateScheduleChecked(doc);
            schedule.Name = ScheduleName;
        });
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CloseViews()
    {
        Doc.Close(false);
    }

    protected bool ViewExists(string name)
    {
        return new FilteredElementCollector(Doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .Any(v => v.Name == name);
    }
}
