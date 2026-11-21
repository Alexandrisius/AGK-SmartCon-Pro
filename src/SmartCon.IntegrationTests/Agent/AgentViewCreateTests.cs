using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// G.6 view creation tools: duplicate_view (settings survive, dependent
/// plans, non-duplicatable views), create_view (plan/section/3d/legend with
/// read-back and undo) and set_browser_folder (grouping parameter of the
/// current browser organization, read-back and undo).
/// </summary>
public sealed class AgentViewCreateTests : RevitApiTest
{
    private const string LevelName = "SC_ViewCreate_Level";
    private const string PlanName = "SC_ViewCreate_Plan";

    private Document? _document;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;
    private long _levelId;
    private long _planId;
    private long _templateId;

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasFloorPlanViewType(Doc))
            Skip.Test("В шаблоне проекта по умолчанию нет ViewFamilyType для плана этажа");

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();

        _transactions.RunInTransaction(Doc, "Seed view create model", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 3.0);
            level.Name = LevelName;
            _levelId = level.Id.GetValue();

            var plan = ModelSeed.CreateViewPlanChecked(doc, level.Id, PlanName);
            _planId = plan.Id.GetValue();

            var template = plan.CreateViewTemplate();
            if (template is not null)
            {
                template.Name = "SC_ViewCreate_Template";
                plan.ViewTemplateId = template.Id;
                _templateId = template.Id.GetValue();
            }
        });
    }

    [After(Test)]
    public void Close()
    {
        Doc.Close(false);
    }

    [Test]
    public async Task Duplicate_WithDetailing_KeepsTemplateAndUndoDeletes()
    {
        RequireTemplate();

        var dry = RunDuplicate(
            "{\"viewId\":" + _planId + ",\"newName\":\"SC_ViewCreate_Copy\",\"dryRun\":true}");
        await Assert.That(dry.Success).IsTrue();
        await Assert.That(FindViewByName("SC_ViewCreate_Copy")).IsNull();

        var apply = RunDuplicate(
            "{\"viewId\":" + _planId + ",\"newName\":\"SC_ViewCreate_Copy\",\"dryRun\":false}");
        await Assert.That(apply.Success).IsTrue();

        var copyId = Data(apply).GetProperty("viewId").GetInt64();
        var copy = GetView(copyId);
        await Assert.That(copy.Name).IsEqualTo("SC_ViewCreate_Copy");
        await Assert.That(copy.ViewTemplateId).IsEqualTo(GetView(_planId).ViewTemplateId);
        await Assert.That(Data(apply).GetProperty("templateMatchesSource").GetBoolean())
            .IsTrue();

        var undo = RunUndo();
        await Assert.That(undo.Success).IsTrue();
        await Assert.That(Doc.GetElement(ElementIdCompat.Create(copyId))).IsNull();
        await Assert.That(GetView(_planId).Name).IsEqualTo(PlanName);
    }

    [Test]
    public async Task Duplicate_DefaultName_AndNameTakenGuard()
    {
        var apply = RunDuplicate("{\"viewId\":" + _planId + ",\"dryRun\":false}");
        await Assert.That(apply.Success).IsTrue();
        var copyName = Data(apply).GetProperty("name").GetString();
        await Assert.That(copyName).IsEqualTo(PlanName + " (копия)");

        var taken = RunDuplicate(
            "{\"viewId\":" + _planId + ",\"newName\":\"" + copyName + "\",\"dryRun\":false}");
        await Assert.That(taken.Success).IsFalse();
        await Assert.That(taken.ErrorCode).IsEqualTo("view_name_taken");
    }

    [Test]
    public async Task Duplicate_Dependent_OnPlan_CreatesDependentAndUndo()
    {
        var apply = RunDuplicate(
            "{\"viewId\":" + _planId + ",\"option\":\"dependent\",\"newName\":\"SC_ViewCreate_Dep\",\"dryRun\":false}");
        await Assert.That(apply.Success).IsTrue();

        var copyId = Data(apply).GetProperty("viewId").GetInt64();
        await Assert.That(GetView(copyId)).IsTypeOf<ViewPlan>();
        await Assert.That(GetView(copyId).Name).IsEqualTo("SC_ViewCreate_Dep");

        var undo = RunUndo();
        await Assert.That(undo.Success).IsTrue();
        await Assert.That(Doc.GetElement(ElementIdCompat.Create(copyId))).IsNull();
    }

    [Test]
    public async Task Duplicate_Dependent_OnSection_Refused()
    {
        var section = CreateSectionDirect("SC_ViewCreate_SectionSrc");
        var result = RunDuplicate(
            "{\"viewId\":" + section + ",\"option\":\"dependent\",\"newName\":\"SC_No\",\"dryRun\":false}");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("dependent_not_supported");
    }

    [Test]
    public async Task Duplicate_Sheet_IsNotDuplicatable()
    {
        var sheetId = TryCreateSheet();
        if (sheetId is null)
            Skip.Test("В библиотеке Revit не найден Title Block для создания листа");

        var result = RunDuplicate(
            "{\"viewId\":" + sheetId + ",\"newName\":\"SC_SheetCopy\",\"dryRun\":false}");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("view_not_duplicatable");
    }

    [Test]
    public async Task Create_Plan_ReadBackAndUndoDeletes()
    {
        var apply = RunCreate(
            "{\"kind\":\"plan\",\"name\":\"SC_NewPlan\",\"levelId\":" + _levelId +
            ",\"dryRun\":false}");

        await Assert.That(apply.Success).IsTrue();
        var viewId = Data(apply).GetProperty("viewId").GetInt64();
        await Assert.That(Data(apply).GetProperty("kind").GetString()).IsEqualTo("plan");
        await Assert.That(GetView(viewId).Name).IsEqualTo("SC_NewPlan");
        await Assert.That(GetView(viewId)).IsTypeOf<ViewPlan>();
        await Assert.That(Data(apply).GetProperty("levelId").GetInt64()).IsEqualTo(_levelId);

        var taken = RunCreate(
            "{\"kind\":\"plan\",\"name\":\"SC_NewPlan\",\"levelId\":" + _levelId +
            ",\"dryRun\":false}");
        await Assert.That(taken.Success).IsFalse();
        await Assert.That(taken.ErrorCode).IsEqualTo("view_name_taken");

        var noLevel = RunCreate("{\"kind\":\"plan\",\"name\":\"SC_No\",\"levelId\":987654321}");
        await Assert.That(noLevel.Success).IsFalse();
        await Assert.That(noLevel.ErrorCode).IsEqualTo("level_not_found");

        var undo = RunUndo();
        await Assert.That(undo.Success).IsTrue();
        await Assert.That(Doc.GetElement(ElementIdCompat.Create(viewId))).IsNull();
    }

    [Test]
    public async Task Create_Section_North_ReadBackAndUndo()
    {
        var apply = RunCreate(
            "{\"kind\":\"section\",\"name\":\"SC_NewSection\",\"levelId\":" + _levelId +
            ",\"pointMmX\":5000,\"pointMmY\":10000,\"direction\":\"north\"," +
            "\"lengthMm\":12000,\"dryRun\":false}");

        await Assert.That(apply.Success).IsTrue();
        var viewId = Data(apply).GetProperty("viewId").GetInt64();
        var section = GetView(viewId);
        await Assert.That(section).IsTypeOf<ViewSection>();
        await Assert.That(section.Name).IsEqualTo("SC_NewSection");

        var undo = RunUndo();
        await Assert.That(undo.Success).IsTrue();
        await Assert.That(Doc.GetElement(ElementIdCompat.Create(viewId))).IsNull();
    }

    [Test]
    public async Task Create_3D_ReadBackAndUndo()
    {
        var apply = RunCreate(
            "{\"kind\":\"3d\",\"name\":\"SC_New3D\",\"dryRun\":false}");

        await Assert.That(apply.Success).IsTrue();
        var viewId = Data(apply).GetProperty("viewId").GetInt64();
        await Assert.That(GetView(viewId)).IsTypeOf<View3D>();
        await Assert.That(GetView(viewId).Name).IsEqualTo("SC_New3D");

        var undo = RunUndo();
        await Assert.That(undo.Success).IsTrue();
        await Assert.That(Doc.GetElement(ElementIdCompat.Create(viewId))).IsNull();
    }

    [Test]
    public async Task Create_Legend_DuplicatesExistingOrHonestError()
    {
        var apply = RunCreate(
            "{\"kind\":\"legend\",\"name\":\"SC_NewLegend\",\"dryRun\":false}");

        var legend = FindLegend();
        if (legend is null)
        {
            await Assert.That(apply.Success).IsFalse();
            await Assert.That(apply.ErrorCode).IsEqualTo("legend_source_missing");
            return;
        }

        await Assert.That(apply.Success).IsTrue();
        var viewId = Data(apply).GetProperty("viewId").GetInt64();
        await Assert.That(GetView(viewId).ViewType).IsEqualTo(ViewType.Legend);
        await Assert.That(GetView(viewId).Name).IsEqualTo("SC_NewLegend");

        var undo = RunUndo();
        await Assert.That(undo.Success).IsTrue();
        await Assert.That(Doc.GetElement(ElementIdCompat.Create(viewId))).IsNull();
    }

    [Test]
    public async Task SetBrowserFolder_ApplyReadBackAndUndo_OrReadOnlyGuard()
    {
        var organization = BrowserOrganization.GetCurrentBrowserOrganizationForViews(Doc);
        var plan = GetView(_planId);
        Parameter? parameter = null;
        var writable = organization is not null
                       && SetBrowserFolderTool.TryGetGroupingParameter(
                           Doc, organization, plan, out parameter, out _)
                       && !parameter.IsReadOnly;

        var apply = RunFolder(
            "{\"viewIds\":[" + _planId + "],\"folder\":\"SC_Папка\",\"dryRun\":false}");

        if (!writable)
        {
            await Assert.That(apply.Success).IsFalse();
            await Assert.That(apply.ErrorCode).IsEqualTo("browser_folder_parameter_read_only");
            return;
        }

        await Assert.That(apply.Success).IsTrue();
        await Assert.That(Data(apply).GetProperty("changed").GetArrayLength()).IsEqualTo(1);
        await Assert.That(parameter!.AsString()).IsEqualTo("SC_Папка");

        var undo = RunUndo();
        await Assert.That(undo.Success).IsTrue();
        await Assert.That(parameter.AsString() ?? string.Empty).IsEmpty();
    }

    private ToolResult RunDuplicate(string json) =>
        Run(new DuplicateViewTool(new StubRevitContext(Doc), _transactions!, _changeSets!), json);

    private ToolResult RunCreate(string json) =>
        Run(new CreateViewTool(new StubRevitContext(Doc), _transactions!, _changeSets!), json);

    private ToolResult RunFolder(string json) =>
        Run(new SetBrowserFolderTool(new StubRevitContext(Doc), _transactions!, _changeSets!), json);

    private ToolResult RunUndo() =>
        Run(new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!),
            "{\"dryRun\":false}");

    private static ToolResult Run(AgentWriteToolBase tool, string json) =>
        tool.ExecuteForTests(Parse(json), approved: true, CancellationToken.None);

    private long CreateSectionDirect(string name)
    {
        long id = 0;
        _transactions!.RunInTransaction(Doc, "Seed section", doc =>
        {
            var vft = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .First(v => v.ViewFamily == ViewFamily.Section);

            var transform = Transform.Identity;
            transform.BasisX = XYZ.BasisX;
            transform.BasisY = XYZ.BasisZ;
            transform.BasisZ = XYZ.BasisY;
            transform.Origin = new XYZ(0, 0, 0);

            var box = new BoundingBoxXYZ
            {
                Transform = transform,
                Min = new XYZ(-5, 0, -5),
                Max = new XYZ(5, 3, 5)
            };
            var section = ViewSection.CreateSection(doc, vft.Id, box);
            section.Name = name;
            id = section.Id.GetValue();
        });
        return id;
    }

    private long? TryCreateSheet()
    {
        var titleBlock = ModelSeed.FindTitleBlockTypeId(Doc);
        if (titleBlock is null)
        {
            var path = ModelSeed.FindTitleBlockFamilyFile(Doc.Application.VersionNumber);
            if (path is not null)
            {
                _transactions!.RunInTransaction(Doc, "Load title block",
                    doc => doc.LoadFamily(path, out _));
                titleBlock = ModelSeed.FindTitleBlockTypeId(Doc);
            }
        }

        if (titleBlock is null)
            return null;

        long id = 0;
        _transactions!.RunInTransaction(Doc, "Seed sheet", doc =>
            id = ViewSheet.Create(doc, titleBlock).Id.GetValue());
        return id;
    }

    private void RequireTemplate()
    {
        if (_templateId == 0)
            Skip.Test("Шаблон вида не создан");
    }

    private View GetView(long viewId) =>
        Doc.GetElement(ElementIdCompat.Create(viewId)) as View
        ?? throw new InvalidOperationException("Вид " + viewId + " не найден");

    private View? FindViewByName(string name)
    {
        foreach (var element in new FilteredElementCollector(Doc).OfClass(typeof(View)))
        {
            if (element is View view
                && string.Equals(view.Name, name, StringComparison.Ordinal))
            {
                return view;
            }
        }

        return null;
    }

    private View? FindLegend()
    {
        foreach (var element in new FilteredElementCollector(Doc).OfClass(typeof(View)))
        {
            if (element is View view
                && view.ViewType == ViewType.Legend
                && !view.IsTemplate)
            {
                return view;
            }
        }

        return null;
    }

    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement Data(ToolResult result) =>
        result.Data ?? throw new InvalidOperationException("Tool result has no data.");
}
