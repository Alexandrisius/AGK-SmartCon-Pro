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
/// Interaction between a view template and the view's own filters (C4-01):
/// assigning a template that includes "V/G Overrides Filters" replaces the
/// view's own filters with the template's list — on assignment, on any later
/// template edit and on turning the inclusion on. The assignment tools must
/// warn; create_view_filter must refuse a filter-controlled view and verify
/// the filter after the commit.
/// </summary>
public sealed class AgentViewTemplateFilterTests : RevitApiTest
{
    private Document? _document;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;
    private long _levelId;
    private long _viewId;
    private long _filterAId;
    private long _templateWithFiltersId;
    private long _templateEmptyFiltersId;
    private long _templateNoControlId;
    private bool _templateSeeded;

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasFloorPlanViewType(Doc))
            Skip.Test("В шаблоне проекта по умолчанию нет ViewFamilyType для плана этажа");

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();

        _transactions.RunInTransaction(Doc, "Seed template/filter model", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 3.0);
            _levelId = level.Id.GetValue();

            var plan = ModelSeed.CreateViewPlanChecked(doc, level.Id, "SC_TF_Plan");
            _viewId = plan.Id.GetValue();

            // The seed must be deterministic: a view without a template assigned by type.
            if (plan.ViewTemplateId != ElementId.InvalidElementId)
                plan.ViewTemplateId = ElementId.InvalidElementId;

            var walls = new List<ElementId> { new ElementId(BuiltInCategory.OST_Walls) };
            var filterA = ParameterFilterElement.Create(doc, "SC_TF_A", walls);
            _filterAId = filterA.Id.GetValue();

            // Include on, filters [A]: CreateViewTemplate takes the settings from the view.
            plan.AddFilter(filterA.Id);
            var withFilters = plan.CreateViewTemplate();

            // Include on, no filters.
            plan.RemoveFilter(filterA.Id);
            var emptyFilters = plan.CreateViewTemplate();

            // Include off, filters [A].
            var noControl = plan.CreateViewTemplate();
            if (withFilters is null || emptyFilters is null || noControl is null)
                return;

            withFilters.Name = "SC_TF_Template_WithFilters";
            emptyFilters.Name = "SC_TF_Template_EmptyFilters";
            noControl.Name = "SC_TF_Template_NoControl";

            noControl.SetNonControlledTemplateParameterIds(
                new List<ElementId> { new ElementId(BuiltInParameter.VIS_GRAPHICS_FILTERS) });
            noControl.AddFilter(filterA.Id);

            _templateWithFiltersId = withFilters.Id.GetValue();
            _templateEmptyFiltersId = emptyFilters.Id.GetValue();
            _templateNoControlId = noControl.Id.GetValue();
            _templateSeeded = true;
        });

        if (!_templateSeeded)
            Skip.Test("CreateViewTemplate не создал шаблон в этом шаблоне проекта");
    }

    [After(Test)]
    public void Close()
    {
        Doc.Close(false);
    }

    private View GetView(long id) => (View)Doc.GetElement(ElementIdCompat.Create(id));

    private static long FilterParameterId() =>
        new ElementId(BuiltInParameter.VIS_GRAPHICS_FILTERS).GetValue();

    /// <summary>
    /// The fact of the "V/G Overrides Filters" inclusion: the parameter is in the
    /// template's whitelist and is NOT among the non-controlled ones
    /// (GetTemplateParameterIds is a whitelist, not the inclusion fact).
    /// </summary>
    private static bool ControlsFilters(View template)
    {
        var nonControlled = template.GetNonControlledTemplateParameterIds()
            .Select(id => id.GetValue())
            .ToList();
        return template.GetTemplateParameterIds().Any(id => id.GetValue() == FilterParameterId())
               && !nonControlled.Contains(FilterParameterId());
    }

    private static string FilterNames(View view)
    {
        var names = view.GetFilters()
            .Select(id =>
            {
                var name = (view.Document.GetElement(id) as ParameterFilterElement)?.Name;
                return string.IsNullOrEmpty(name) ? id.GetValue().ToString() : name;
            })
            .OrderBy(name => name, StringComparer.Ordinal);
        return string.Join(",", names);
    }

    private void AddOwnFilter(string name)
    {
        _transactions!.RunInTransaction(Doc, "Own filter " + name, doc =>
        {
            var view = GetView(_viewId);
            var filter = ParameterFilterElement.Create(
                doc, name, new List<ElementId> { new ElementId(BuiltInCategory.OST_Walls) });
            view.AddFilter(filter.Id);
        });
    }

    private long AssignTemplate(long templateId)
    {
        _transactions!.RunInTransaction(Doc, "Assign template", doc =>
            GetView(_viewId).ViewTemplateId = ElementIdCompat.Create(templateId));
        return GetView(_viewId).ViewTemplateId.GetValue();
    }

    private static ToolResult Run(AgentWriteToolBase tool, string json) =>
        tool.ExecuteForTests(Parse(json), approved: true, CancellationToken.None);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement Data(ToolResult result) =>
        result.Data ?? throw new InvalidOperationException("Tool result has no data.");

    [Test]
    public async Task Seed_TemplatesHaveTheIntendedFilterControl()
    {
        var withFilters = GetView(_templateWithFiltersId);
        var emptyFilters = GetView(_templateEmptyFiltersId);
        var noControl = GetView(_templateNoControlId);

        await Assert.That(ControlsFilters(withFilters)).IsTrue();
        await Assert.That(withFilters.GetFilters().Any(id => id.GetValue() == _filterAId)).IsTrue();
        await Assert.That(ControlsFilters(emptyFilters)).IsTrue();
        await Assert.That(emptyFilters.GetFilters().Count).IsEqualTo(0);
        await Assert.That(ControlsFilters(noControl)).IsFalse();
        await Assert.That(noControl.GetFilters().Any(id => id.GetValue() == _filterAId)).IsTrue();
        await Assert.That(GetView(_viewId).GetFilters().Count).IsEqualTo(0);
    }

    /// <summary>
    /// The core of C4-01: assigning a template that includes "V/G Overrides
    /// Filters" to a view with an OWN filter — the own set is replaced by the
    /// template's set.
    /// </summary>
    [Test]
    public async Task AssigningFilterControllingTemplate_ReplacesOwnViewFilters()
    {
        AddOwnFilter("SC_TF_B");
        var applied = AssignTemplate(_templateWithFiltersId);
        var view = GetView(_viewId);
        var after = FilterNames(view);

        await Assert.That(applied).IsEqualTo(_templateWithFiltersId);
        await Assert.That(view.GetFilters().Any(id => id.GetValue() == _filterAId)).IsTrue();
        await Assert.That(view.GetFilters().Count).IsEqualTo(1);
        await Assert.That(after).IsEqualTo("SC_TF_A");
    }

    /// <summary>
    /// Variant of the hypothesis: the template includes "V/G Overrides Filters"
    /// but has no filters of its own — the view's own filters still go (the view
    /// receives the template's empty set).
    /// </summary>
    [Test]
    public async Task AssigningFilterControllingTemplateWithoutFilters_ClearsOwnViewFilters()
    {
        AddOwnFilter("SC_TF_Cleared");
        AssignTemplate(_templateEmptyFiltersId);

        await Assert.That(GetView(_viewId).GetFilters().Count).IsEqualTo(0);
    }

    /// <summary>
    /// Control: a template with the filters inclusion OFF does not take the
    /// view's own filters and does not bring its own.
    /// </summary>
    [Test]
    public async Task AssigningTemplateWithoutFilterControl_KeepsOwnViewFilters()
    {
        AddOwnFilter("SC_TF_Keep");
        var before = FilterNames(GetView(_viewId));
        AssignTemplate(_templateNoControlId);
        var after = FilterNames(GetView(_viewId));

        await Assert.That(after).IsEqualTo(before);
        await Assert.That(after).IsEqualTo("SC_TF_Keep");
    }

    /// <summary>
    /// The reverse order (template → filter) at the Revit level: a filter added
    /// to the view AFTER the template assignment stays on the view, including
    /// after re-assigning the same template.
    /// </summary>
    [Test]
    public async Task FilterAddedAfterTemplateAssignment_StaysOnView()
    {
        AssignTemplate(_templateWithFiltersId);
        AddOwnFilter("SC_TF_After");
        var after = FilterNames(GetView(_viewId));
        AssignTemplate(_templateWithFiltersId);
        var afterReassign = FilterNames(GetView(_viewId));

        await Assert.That(after).Contains("SC_TF_After");
        await Assert.That(afterReassign).IsEqualTo(after);
    }

    /// <summary>
    /// Any edit of the template resets the own filters the views of that
    /// template had added (they keep only the template's list).
    /// </summary>
    [Test]
    public async Task TemplateEditAfterOwnFilter_RemovesTheOwnFilter()
    {
        AssignTemplate(_templateWithFiltersId);
        AddOwnFilter("SC_TF_Edited");
        var before = FilterNames(GetView(_viewId));

        _transactions!.RunInTransaction(Doc, "Edit template", doc =>
            GetView(_templateWithFiltersId).SetFilterVisibility(
                ElementIdCompat.Create(_filterAId), false));

        var after = FilterNames(GetView(_viewId));
        await Assert.That(before).IsEqualTo("SC_TF_A,SC_TF_Edited");
        await Assert.That(after).IsEqualTo("SC_TF_A");
    }

    /// <summary>
    /// Toggling the "V/G Overrides Filters" inclusion of the template resets the
    /// own filters the views of that template had added.
    /// </summary>
    [Test]
    public async Task IncludeToggleAfterOwnFilter_RemovesTheOwnFilter()
    {
        AssignTemplate(_templateWithFiltersId);
        AddOwnFilter("SC_TF_Toggled");
        var before = FilterNames(GetView(_viewId));

        _transactions!.RunInTransaction(Doc, "Toggle include", doc =>
        {
            var template = GetView(_templateWithFiltersId);
            var ids = new List<ElementId> { new ElementId(BuiltInParameter.VIS_GRAPHICS_FILTERS) };
            template.SetNonControlledTemplateParameterIds(ids);
            template.SetNonControlledTemplateParameterIds(new List<ElementId>());
        });

        var after = FilterNames(GetView(_viewId));
        await Assert.That(before).IsEqualTo("SC_TF_A,SC_TF_Toggled");
        await Assert.That(after).IsEqualTo("SC_TF_A");
    }

    /// <summary>
    /// C4-01(a): apply_view_template must name the view's own filters the
    /// template replaces — a warning, an ownFiltersReplaced data entry and a
    /// changed[] row that carries them.
    /// </summary>
    [Test]
    public async Task ApplyViewTemplate_OwnFiltersReplaced_WarnsAndReports()
    {
        AddOwnFilter("SC_TF_B");

        var tool = new ApplyViewTemplateTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var result = Run(tool,
            "{\"items\":[{\"viewId\":" + _viewId + ",\"templateId\":" + _templateWithFiltersId +
            "}],\"dryRun\":false}");

        await Assert.That(result.Success).IsTrue();

        var data = Data(result);
        await Assert.That(data.GetProperty("ownFiltersReplaced").GetArrayLength()).IsEqualTo(1);
        var entry = data.GetProperty("ownFiltersReplaced")[0];
        await Assert.That(entry.GetProperty("viewId").GetInt64()).IsEqualTo(_viewId);
        await Assert.That(entry.GetProperty("templateId").GetInt64()).IsEqualTo(_templateWithFiltersId);
        await Assert.That(entry.GetProperty("filters")[0].GetString()).IsEqualTo("SC_TF_B");

        var changed = data.GetProperty("changed");
        await Assert.That(changed.GetArrayLength()).IsGreaterThan(0);
        var ownFiltersOnRow = changed.EnumerateArray()
            .Where(row => row.TryGetProperty("replacedOwnFilters", out _))
            .ToList();
        await Assert.That(ownFiltersOnRow.Count).IsEqualTo(1);
        await Assert.That(ownFiltersOnRow[0].GetProperty("replacedOwnFilters")[0].GetString())
            .IsEqualTo("SC_TF_B");

        var warned = result.Warnings.Any(w =>
            w.Contains("loses its own view filters", StringComparison.Ordinal)
            && w.Contains("SC_TF_B", StringComparison.Ordinal));
        await Assert.That(warned).IsTrue();

        // The takeover itself is the Revit behaviour: the view now has the template's list.
        await Assert.That(FilterNames(GetView(_viewId))).IsEqualTo("SC_TF_A");
    }

    /// <summary>
    /// C4-01(a): the dry-run delta of apply_view_template must show that the
    /// template controls filters and which own filters would be replaced.
    /// </summary>
    [Test]
    public async Task ApplyViewTemplate_DryRun_DeltaReportsFilterTakeover()
    {
        AddOwnFilter("SC_TF_B");

        var tool = new ApplyViewTemplateTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var result = Run(tool,
            "{\"items\":[{\"viewId\":" + _viewId + ",\"templateId\":" + _templateWithFiltersId +
            "}],\"dryRun\":true}");

        await Assert.That(result.Success).IsTrue();

        var delta = Data(result).GetProperty("delta");
        await Assert.That(delta.GetArrayLength()).IsEqualTo(1);
        var row = delta[0];
        await Assert.That(row.GetProperty("viewId").GetInt64()).IsEqualTo(_viewId);
        await Assert.That(row.GetProperty("templateControlsFilters").GetBoolean()).IsTrue();
        await Assert.That(row.GetProperty("ownFiltersReplaced")[0].GetString()).IsEqualTo("SC_TF_B");

        // Nothing was modified by the dry run.
        await Assert.That(FilterNames(GetView(_viewId))).IsEqualTo("SC_TF_B");
    }

    /// <summary>
    /// C4-01(a): turning "filters" on in set_view_template_inclusion must name
    /// the views that lose their own filters.
    /// </summary>
    [Test]
    public async Task SetViewTemplateInclusion_EnablingFilters_WarnsAboutLostOwnFilters()
    {
        AssignTemplate(_templateNoControlId);
        AddOwnFilter("SC_TF_Keep");

        var tool = new SetViewTemplateInclusionTool(
            new StubRevitContext(Doc), _transactions!, _changeSets!);
        var result = Run(tool,
            "{\"templateId\":" + _templateNoControlId +
            ",\"parameters\":[\"filters\"],\"included\":true,\"dryRun\":false}");

        await Assert.That(result.Success).IsTrue();

        var data = Data(result);
        await Assert.That(data.GetProperty("ownFiltersReplaced").GetArrayLength()).IsEqualTo(1);
        await Assert.That(data.GetProperty("ownFiltersReplaced")[0]
                .GetProperty("filters")[0].GetString())
            .IsEqualTo("SC_TF_Keep");

        var warned = result.Warnings.Any(w =>
            w.Contains("SC_TF_Keep", StringComparison.Ordinal));
        await Assert.That(warned).IsTrue();

        // The inclusion made the template the owner: the view keeps the template's list.
        await Assert.That(FilterNames(GetView(_viewId))).IsEqualTo("SC_TF_A");
    }

    /// <summary>
    /// C4-01(a): create_views_batch with a filter-controlling template must warn
    /// and mark templateControlsFilters on the created view.
    /// </summary>
    [Test]
    public async Task CreateViewsBatch_FilterControllingTemplate_WarnsAndMarks()
    {
        var tool = new CreateViewsBatchTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var result = Run(tool,
            "{\"items\":[{\"kind\":\"plan\",\"name\":\"SC_TF_New\",\"levelId\":" + _levelId +
            ",\"templateId\":" + _templateWithFiltersId + "}],\"dryRun\":false}");

        await Assert.That(result.Success).IsTrue();

        var data = Data(result);
        await Assert.That(data.GetProperty("created").GetArrayLength()).IsEqualTo(1);
        await Assert.That(data.GetProperty("created")[0]
                .GetProperty("templateControlsFilters").GetBoolean())
            .IsTrue();

        var warned = result.Warnings.Any(w =>
            w.Contains("SC_TF_Template_WithFilters", StringComparison.Ordinal));
        await Assert.That(warned).IsTrue();
    }

    /// <summary>
    /// C4-01(в): create_view_filter on a view whose template controls filters
    /// must not report an applied filter silently.
    /// </summary>
    [Test]
    public async Task CreateViewFilter_OnFilterControlledView_FailsHonestly()
    {
        AssignTemplate(_templateWithFiltersId);

        var tool = new CreateViewFilterTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var result = Run(tool,
            "{\"viewIds\":[" + _viewId + "],\"categories\":[\"OST_Walls\"]," +
            "\"conditions\":[{\"parameter\":\"ALL_MODEL_INSTANCE_COMMENTS\"," +
            "\"op\":\"eq\",\"value\":\"SC_TF_X\"}],\"dryRun\":false}");

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("view_controlled_by_template");
    }
}
