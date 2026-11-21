using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// F2 (#457/#456): apply_view_template reports settings the template leaves
/// per-view because their Include is off, supports an exception profile
/// (exceptViewIds/exceptNameContains), and selects views by the project
/// parameter «Раздел»; list_views exposes the same value as the section field.
/// The shared parameter file is swapped to a temp file, never the user's one.
/// </summary>
public sealed class AgentViewTemplateSelectionTests : RevitApiTest
{
    private const string Prefix = "SC_TplSel_";
    private const string PlanAName = Prefix + "PlanA";
    private const string PlanBName = Prefix + "PlanB";
    private const string PlanCName = Prefix + "PlanC";

    private Document? _document;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;
    private SharedParameterFileScope? _sharedFile;
    private long _levelId;
    private long _planAId;
    private long _planBId;
    private long _planCId;
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
        _sharedFile = SharedParameterFileScope.Create(Application);

        _transactions.RunInTransaction(Doc, "Seed template selection views", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 3.0);
            _levelId = level.Id.GetValue();

            var planA = ModelSeed.CreateViewPlanChecked(doc, level.Id, PlanAName);
            _planAId = planA.Id.GetValue();

            var planB = ModelSeed.CreateViewPlanChecked(doc, level.Id, PlanBName);
            _planBId = planB.Id.GetValue();

            var planC = ModelSeed.CreateViewPlanChecked(doc, level.Id, PlanCName);
            _planCId = planC.Id.GetValue();

            _templateId = planA.CreateViewTemplate()?.Id.GetValue() ?? 0;
        });
    }

    [After(Test)]
    public void Close()
    {
        _sharedFile?.Dispose();
        Doc.Close(false);
    }

    [Test]
    public async Task ApplyViewTemplate_IncludeOffViewRange_ReportsSkippedSections()
    {
        if (_templateId == 0)
            Skip.Test("Шаблон вида не создан");

        DetachViewRangeOnTemplate();
        SetCustomRange(_planBId);

        var result = ApplyTemplate(
            "{\"items\":[{\"viewId\":" + _planBId + ",\"templateId\":" + _templateId +
            "}],\"dryRun\":false}");

        await Assert.That(result.Success).IsTrue();
        var changed = result.Data!.Value.GetProperty("changed");
        await Assert.That(changed.GetArrayLength()).IsEqualTo(1);
        var sections = SectionsOf(changed[0].GetProperty("skippedByTemplateInclude"));
        await Assert.That(sections).Contains("viewRange");

        var summary = result.Data.Value.GetProperty("templateIncludeSkips");
        await Assert.That(summary.GetArrayLength()).IsGreaterThanOrEqualTo(1);
        await Assert.That(summary[0].GetString()).Contains("1 view(s): viewRange left as-is");
        await Assert.That(summary[0].GetString()).Contains("Include off on template");

        await Assert.That(GetView(_planBId).ViewTemplateId.GetValue()).IsEqualTo(_templateId);
        // The view keeps its own range — the case-3 behavior the report must
        // make visible instead of a clean success.
        var topMm = ReadTopOffsetMm(_planBId);
        await Assert.That(Math.Round(topMm)).IsEqualTo(2500);
    }

    [Test]
    public async Task ApplyViewTemplate_IncludeOffViewRange_DryRun_ReportsWouldSkip()
    {
        if (_templateId == 0)
            Skip.Test("Шаблон вида не создан");

        DetachViewRangeOnTemplate();

        var result = ApplyTemplate(
            "{\"items\":[{\"viewId\":" + _planBId + ",\"templateId\":" + _templateId +
            "}],\"dryRun\":true}");

        await Assert.That(result.Success).IsTrue();
        var changed = result.Data!.Value.GetProperty("changed");
        await Assert.That(changed.GetArrayLength()).IsEqualTo(1);
        var sections = SectionsOf(changed[0].GetProperty("skippedByTemplateInclude"));
        await Assert.That(sections).Contains("viewRange");

        var summary = result.Data.Value.GetProperty("templateIncludeSkips");
        await Assert.That(summary[0].GetString()).Contains("viewRange would be left as-is");
        await Assert.That(GetView(_planBId).ViewTemplateId)
            .IsEqualTo(ElementId.InvalidElementId);
    }

    [Test]
    public async Task ApplyViewTemplate_ExceptViewIds_RemovesViewsFromSelection()
    {
        var result = ApplyTemplate(
            "{\"select\":{\"type\":\"FloorPlan\",\"nameContains\":\"" + Prefix + "\"}," +
            "\"templateId\":" + _templateId + ",\"exceptViewIds\":[" + _planCId +
            "],\"dryRun\":true}");

        await Assert.That(result.Success).IsTrue();
        var selected = IdsOf(result.Data!.Value.GetProperty("selectedViewIds"));
        await Assert.That(selected).Contains(_planBId);
        await Assert.That(selected.Contains(_planCId)).IsFalse();
        var excluded = IdsOf(result.Data.Value.GetProperty("excludedViewIds"));
        await Assert.That(excluded.Count).IsEqualTo(1);
        await Assert.That(excluded[0]).IsEqualTo(_planCId);
    }

    [Test]
    public async Task ApplyViewTemplate_ExceptNameContains_FiltersAndAllExcludedFails()
    {
        var byName = ApplyTemplate(
            "{\"select\":{\"type\":\"FloorPlan\",\"nameContains\":\"" + Prefix + "\"}," +
            "\"templateId\":" + _templateId + ",\"exceptNameContains\":\"PlanC\",\"dryRun\":true}");

        await Assert.That(byName.Success).IsTrue();
        var selected = IdsOf(byName.Data!.Value.GetProperty("selectedViewIds"));
        await Assert.That(selected.Contains(_planCId)).IsFalse();
        await Assert.That(selected).Contains(_planBId);
        await Assert.That(IdsOf(byName.Data.Value.GetProperty("excludedViewIds")))
            .IsEquivalentTo(new List<long> { _planCId });

        var exhausted = ApplyTemplate(
            "{\"select\":{\"type\":\"FloorPlan\",\"nameContains\":\"" + PlanBName + "\"}," +
            "\"templateId\":" + _templateId + ",\"exceptViewIds\":[" + _planBId +
            "],\"dryRun\":true}");

        await Assert.That(exhausted.Success).IsFalse();
        await Assert.That(exhausted.ErrorCode).IsEqualTo("no_items");
        await Assert.That(exhausted.Message).Contains("excluded");
    }

    [Test]
    public async Task ListViews_Section_ReadsProjectParameterValue()
    {
        BindSectionParameter();
        SetSection(_planBId, "АР");
        SetSection(_planCId, "ОВ");

        var listed = new ListViewsTool(new StubRevitContext(Doc))
            .ExecuteForTests(
                Doc,
                Parse("{\"nameContains\":\"" + Prefix + "\",\"limit\":50}"),
                CancellationToken.None);

        await Assert.That(listed.Success).IsTrue();
        var views = listed.Data!.Value.GetProperty("views");
        await Assert.That(CardOf(views, _planBId).GetProperty("section").GetString())
            .IsEqualTo("АР");
        await Assert.That(CardOf(views, _planCId).GetProperty("section").GetString())
            .IsEqualTo("ОВ");
        var planA = CardOf(views, _planAId);
        await Assert.That(planA.TryGetProperty("section", out var unused)
                          && unused.ValueKind != JsonValueKind.Null)
            .IsFalse();
    }

    [Test]
    public async Task ApplyViewTemplate_SectionSelector_AppliesOnlyToSectionViews()
    {
        if (_templateId == 0)
            Skip.Test("Шаблон вида не создан");

        BindSectionParameter();
        SetSection(_planBId, "АР");
        SetSection(_planCId, "ОВ");

        var result = ApplyTemplate(
            "{\"select\":{\"type\":\"FloorPlan\",\"section\":\"АР\"}," +
            "\"templateId\":" + _templateId + ",\"dryRun\":false}");

        await Assert.That(result.Success).IsTrue();
        var selected = IdsOf(result.Data!.Value.GetProperty("selectedViewIds"));
        await Assert.That(selected.Count).IsEqualTo(1);
        await Assert.That(selected[0]).IsEqualTo(_planBId);
        await Assert.That(GetView(_planBId).ViewTemplateId.GetValue()).IsEqualTo(_templateId);
        await Assert.That(GetView(_planCId).ViewTemplateId)
            .IsEqualTo(ElementId.InvalidElementId);
    }

    [Test]
    public async Task ApplyViewTemplate_SectionSelector_WithoutParameter_FailsTyped()
    {
        var result = ApplyTemplate(
            "{\"select\":{\"type\":\"FloorPlan\",\"section\":\"АР\"}," +
            "\"templateId\":" + _templateId + ",\"dryRun\":true}");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("section_parameter_missing");
        await Assert.That(result.Message).Contains("add_project_shared_parameter");
    }

    /// <summary>
    /// AgentViewSelector.Resolve supports select.section, so every select-contract
    /// tool must accept it in its schema too (additionalProperties:false +
    /// AgentToolSchemaValidator would reject the call otherwise — validator
    /// finding for #456).
    /// </summary>
    [Test]
    public async Task SelectSection_AcceptedBySchemaValidator_InAllSelectTools()
    {
        var context = new StubRevitContext(Doc);
        var cases = new List<(string Tool, string Schema, string Arguments)>
        {
            ("set_view_crop",
                new SetViewCropTool(context, _transactions!, _changeSets!).Definition.InputSchemaJson,
                "{\"select\":{\"type\":\"FloorPlan\",\"section\":\"АР\"}," +
                "\"crop\":{\"active\":true},\"dryRun\":true}"),
            ("set_view_visibility",
                new SetViewVisibilityTool(context, _transactions!, _changeSets!).Definition.InputSchemaJson,
                "{\"select\":{\"type\":\"FloorPlan\",\"section\":\"АР\"}," +
                "\"visibility\":{\"categories\":[{\"category\":\"OST_Grids\"}]}," +
                "\"dryRun\":true}"),
            ("set_view_range",
                new SetViewRangeTool(context, _transactions!, _changeSets!).Definition.InputSchemaJson,
                "{\"select\":{\"type\":\"FloorPlan\",\"section\":\"АР\"}," +
                "\"viewRange\":{\"top\":{\"levelId\":" + _levelId + ",\"offsetMm\":100}}," +
                "\"dryRun\":true}"),
            ("configure_views",
                new ConfigureViewsTool(context, _transactions!, _changeSets!).Definition.InputSchemaJson,
                "{\"select\":{\"type\":\"FloorPlan\",\"section\":\"АР\"}," +
                "\"settings\":{\"crop\":{\"active\":true}},\"dryRun\":true}"),
            ("assign_scope_box",
                new AssignScopeBoxTool(context, _transactions!, _changeSets!).Definition.InputSchemaJson,
                "{\"viewSelect\":{\"type\":\"FloorPlan\",\"section\":\"АР\"}," +
                "\"clear\":true,\"dryRun\":true}"),
            ("copy_crop_from_view",
                new CopyCropFromViewTool(context, _transactions!, _changeSets!).Definition.InputSchemaJson,
                "{\"sourceViewId\":" + _planAId + "," +
                "\"targetSelect\":{\"type\":\"FloorPlan\",\"section\":\"АР\"},\"dryRun\":true}"),
            ("apply_view_template",
                new ApplyViewTemplateTool(context, _transactions!, _changeSets!).Definition.InputSchemaJson,
                "{\"select\":{\"type\":\"FloorPlan\",\"section\":\"АР\"}," +
                "\"templateId\":" + _templateId + ",\"dryRun\":true}")
        };

        foreach (var (tool, schema, arguments) in cases)
        {
            var errors = AgentToolSchemaValidator.Validate(schema, arguments);
            await Assert.That(errors.Count)
                .IsEqualTo(0)
                .Because(tool + " must accept select.section: " +
                          string.Join("; ", errors));
        }
    }

    private ToolResult ApplyTemplate(string json) =>
        new ApplyViewTemplateTool(new StubRevitContext(Doc), _transactions!, _changeSets!)
            .ExecuteForTests(Parse(json), approved: true, CancellationToken.None);

    private void DetachViewRangeOnTemplate()
    {
        var result = new SetViewTemplateInclusionTool(
                new StubRevitContext(Doc), _transactions!, _changeSets!)
            .ExecuteForTests(
                Parse("{\"templateId\":" + _templateId +
                      ",\"parameters\":[\"viewRange\"],\"dryRun\":false}"),
                approved: true,
                CancellationToken.None);
        if (!result.Success)
            throw new InvalidOperationException(
                "set_view_template_inclusion: " + result.ErrorCode + ": " + result.Message);
    }

    private void SetCustomRange(long viewId)
    {
        var result = new SetViewRangeTool(
                new StubRevitContext(Doc), _transactions!, _changeSets!)
            .ExecuteForTests(
                Parse("{\"items\":[{\"viewId\":" + viewId +
                      ",\"viewRange\":{\"top\":{\"levelId\":" + _levelId + ",\"offsetMm\":2500}," +
                      "\"cutPlane\":{\"levelId\":" + _levelId + ",\"offsetMm\":1000}," +
                      "\"bottom\":{\"levelId\":" + _levelId + ",\"offsetMm\":0}," +
                      "\"viewDepth\":{\"levelId\":" + _levelId + ",\"offsetMm\":-1200}}}]," +
                      "\"dryRun\":false}"),
                approved: true,
                CancellationToken.None);
        if (!result.Success)
            throw new InvalidOperationException(
                "set_view_range: " + result.ErrorCode + ": " + result.Message);
    }

    private void BindSectionParameter()
    {
        var result = new AddProjectSharedParameterTool(
                new StubRevitContext(Doc), _transactions!, _changeSets!)
            .ExecuteForTests(
                Parse("{\"name\":\"Раздел\",\"spec\":\"text\",\"categories\":[\"OST_Views\"]," +
                      "\"createDefinition\":true,\"dryRun\":false}"),
                approved: true,
                CancellationToken.None);
        if (!result.Success)
            throw new InvalidOperationException(
                "add_project_shared_parameter: " + result.ErrorCode + ": " + result.Message +
                " data=" + result.Data);
    }

    private void SetSection(long viewId, string value)
    {
        var result = new SetParametersTool(
                new StubRevitContext(Doc), _transactions!, _changeSets!)
            .ExecuteForTests(
                Parse("{\"items\":[{\"elementId\":" + viewId +
                      ",\"parameter\":\"Раздел\",\"value\":\"" + value + "\"}],\"dryRun\":false}"),
                approved: true,
                CancellationToken.None);
        if (!result.Success)
            throw new InvalidOperationException(
                "set_parameters: " + result.ErrorCode + ": " + result.Message);
    }

    private View GetView(long id) => (View)Doc.GetElement(ElementIdCompat.Create(id));

    private double ReadTopOffsetMm(long viewId)
    {
        var range = ((ViewPlan)GetView(viewId)).GetViewRange();
        return UnitUtils.ConvertFromInternalUnits(
            range.GetOffset(PlanViewPlane.TopClipPlane),
            UnitTypeId.Millimeters);
    }

    private static List<string> SectionsOf(JsonElement array)
    {
        var sections = new List<string>();
        foreach (var item in array.EnumerateArray())
            sections.Add(item.GetString()!);
        return sections;
    }

    private static List<long> IdsOf(JsonElement array)
    {
        var ids = new List<long>();
        foreach (var item in array.EnumerateArray())
            ids.Add(item.GetInt64());
        return ids;
    }

    private static JsonElement CardOf(JsonElement views, long viewId)
    {
        foreach (var card in views.EnumerateArray())
        {
            if (card.GetProperty("viewId").GetInt64() == viewId)
                return card;
        }

        throw new InvalidOperationException("View " + viewId + " was not listed.");
    }

    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();
}
