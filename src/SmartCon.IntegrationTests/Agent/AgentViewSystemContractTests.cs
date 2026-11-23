using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// The owner's system-view contract: an allow-list isolates a view instead of
/// a blocklist that misses a stair category; categories the API cannot toggle
/// are reported in notHidable[]; a 3D view is created with a detail level; and
/// a template created from a view can leave filters to each view.
/// </summary>
public sealed class AgentViewSystemContractTests : RevitApiTest
{
    private Document? _document;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;
    private long _levelId;
    private long _planId;
    private long _view3dAId;
    private long _view3dBId;
    private bool _seeded;

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasFloorPlanViewType(Doc))
            Skip.Test("В шаблоне проекта по умолчанию нет ViewFamilyType для плана этажа");

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();

        _transactions.RunInTransaction(Doc, "Seed system contract", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 2.0);
            _levelId = level.Id.GetValue();
            var plan = ModelSeed.CreateViewPlanChecked(doc, level.Id, "SC_SC_Plan");
            _planId = plan.Id.GetValue();

            var threeD = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .First(vft => vft.ViewFamily == ViewFamily.ThreeDimensional);
            var a = View3D.CreateIsometric(doc, threeD.Id);
            a.Name = "SC_SC_3D_A";
            _view3dAId = a.Id.GetValue();
            var b = View3D.CreateIsometric(doc, threeD.Id);
            b.Name = "SC_SC_3D_B";
            _view3dBId = b.Id.GetValue();

            ModelSeed.TryCreateWall(doc, level.Id);
            _seeded = true;
        });

        if (!_seeded)
            Skip.Test("Не удалось создать тестовую модель");
    }

    [After(Test)]
    public void Close()
    {
        Doc.Close(false);
    }

    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static ToolResult Run(AgentWriteToolBase tool, string json) =>
        tool.ExecuteForTests(Parse(json), approved: true, CancellationToken.None);

    private ToolResult Read(AgentReadToolBase tool, string json) =>
        tool.ExecuteForTests(Doc, Parse(json), CancellationToken.None);

    private View View(long id) => Doc.GetElement(ElementIdCompat.Create(id)) as View
        ?? throw new InvalidOperationException("View " + id + " not found");

    [Test]
    public async Task SetViewVisibility_HideAllExcept_HidesEveryOtherCategory()
    {
        var tool = new SetViewVisibilityTool(new StubRevitContext(Doc), _transactions!, _changeSets!);

        var result = Run(
            tool,
            "{\"items\":[{\"viewId\":" + _planId + ",\"visibility\":{}}]," +
            "\"hideAllExcept\":[\"OST_Walls\"],\"dryRun\":false}");

        await Assert.That(result.Success).IsTrue();
        var view = View(_planId);
        var walls = new ElementId(BuiltInCategory.OST_Walls);
        var doors = new ElementId(BuiltInCategory.OST_Doors);
        await Assert.That(view.GetCategoryHidden(walls)).IsFalse();
        await Assert.That(view.GetCategoryHidden(doors)).IsTrue();

        var summary = Read(
            new SummarizeElementsTool(new StubRevitContext(Doc)),
            "{\"viewId\":" + _planId + ",\"groupBy\":\"category\"}");
        await Assert.That(summary.Success).IsTrue();
        var groups = summary.Data!.Value.GetProperty("groups").GetRawText();
        var wallsName = Category.GetCategory(Doc, BuiltInCategory.OST_Walls)?.Name ?? "OST_Walls";
        var doorsName = Category.GetCategory(Doc, BuiltInCategory.OST_Doors)?.Name ?? "OST_Doors";
        await Assert.That(groups.Contains(wallsName)).IsTrue();
        await Assert.That(groups.Contains(doorsName)).IsFalse();
    }

    [Test]
    public async Task SetViewVisibility_UnhideableCategory_ReportedInNotHidable()
    {
        var tool = new SetViewVisibilityTool(new StubRevitContext(Doc), _transactions!, _changeSets!);

        var result = Run(
            tool,
            "{\"items\":[{\"viewId\":" + _planId + ",\"visibility\":{\"categories\":[" +
            "{\"category\":\"OST_StairsRuns\",\"hidden\":true}]}}],\"dryRun\":false}");

        await Assert.That(result.Success).IsTrue();
        var data = result.Data ?? throw new InvalidOperationException("no data");
        await Assert.That(data.TryGetProperty("notHidable", out var notHidable)).IsTrue();
        await Assert.That(notHidable.GetArrayLength()).IsGreaterThan(0);
        var text = notHidable.GetRawText();
        await Assert.That(text.Contains("OST_StairsRuns")).IsTrue();
        await Assert.That(text.Contains("Visibility/Graphics")).IsTrue();
    }

    [Test]
    public async Task CreateView_3D_AppliesDetailLevel_AndMediumWarnsAboutSingleLines()
    {
        var tool = new CreateViewTool(new StubRevitContext(Doc), _transactions!, _changeSets!);

        var fine = Run(
            tool,
            "{\"kind\":\"3d\",\"name\":\"SC_SC_Fine\",\"detailLevel\":\"fine\",\"dryRun\":false}");
        await Assert.That(fine.Success).IsTrue();
        var fineView = View(fine.Data!.Value.GetProperty("viewId").GetInt64());
        await Assert.That(fineView.DetailLevel).IsEqualTo(ViewDetailLevel.Fine);

        var medium = Run(
            tool,
            "{\"kind\":\"3d\",\"name\":\"SC_SC_Medium\",\"dryRun\":false}");
        await Assert.That(medium.Success).IsTrue();
        await Assert.That(medium.Warnings.Any(w => w.Contains("single lines"))).IsTrue();
    }

    [Test]
    public async Task GetViewSettings_Compact_CarriesDetailLevelAndDisplayStyle()
    {
        var settings = Read(
            new GetViewSettingsTool(new StubRevitContext(Doc)),
            "{\"viewId\":" + _view3dAId + "}");

        await Assert.That(settings.Success).IsTrue();
        var data = settings.Data!.Value;
        await Assert.That(data.GetProperty("detailLevel").GetString()).IsNotNull();
        await Assert.That(data.TryGetProperty("displayStyle", out var style)).IsTrue();
        await Assert.That(style.GetString()).IsNotNull();
    }

    [Test]
    public async Task SummarizeElements_LimitAlias_CapsGroups()
    {
        var result = Read(
            new SummarizeElementsTool(new StubRevitContext(Doc)),
            "{\"viewId\":" + _planId + ",\"groupBy\":\"category\",\"limit\":1}");

        await Assert.That(result.Success).IsTrue();
        var groups = result.Data!.Value.GetProperty("groups");
        await Assert.That(groups.GetArrayLength()).IsLessThanOrEqualTo(1);
    }

    [Test]
    public async Task CreateViewTemplate_FiltersDetached_LeavesEachViewItsOwnFilters()
    {
        var tool = new CreateViewTemplateTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);

        var result = Run(
            tool,
            "{\"sourceViewId\":" + _view3dAId + ",\"name\":\"SC_SC_3D_Template\"," +
            "\"viewIds\":[" + _view3dBId + "],\"detachParameters\":[\"filters\"],\"dryRun\":false}");

        await Assert.That(result.Success).IsTrue();
        var data = result.Data ?? throw new InvalidOperationException("no data");
        var templateId = data.GetProperty("templateId").GetInt64();
        var template = View(templateId);
        await Assert.That(template.IsTemplate).IsTrue();
        await Assert.That(View(_view3dBId).ViewTemplateId.GetValue()).IsEqualTo(templateId);

        var filtersId = (long)(int)BuiltInParameter.VIS_GRAPHICS_FILTERS;
        var nonControlled = template.GetNonControlledTemplateParameterIds()
            .Any(id => id.GetValue() == filtersId);
        await Assert.That(nonControlled).IsTrue();
    }
}