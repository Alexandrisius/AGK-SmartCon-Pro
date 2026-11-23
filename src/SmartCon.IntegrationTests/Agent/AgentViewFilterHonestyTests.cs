using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Honesty of the view/filter write tools: create_view_filter must tell the
/// model what its result really is (C4-09 disabled filter, C4-16 dry/apply
/// parity of changed[], C4-17 preview ids and row actions), copy_view_settings
/// must warn when mirrored visibility unhides target categories (DB-15),
/// create_views_batch needs skipExisting (N-ds-3), and the MEP chain tools
/// must explain an empty connector list (C10-02).
/// </summary>
public sealed class AgentViewFilterHonestyTests : RevitApiTest
{
    private Document? _document;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;
    private long _levelId;
    private long _viewId;
    private bool _planSeeded;

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasFloorPlanViewType(Doc))
            Skip.Test("В шаблоне проекта по умолчанию нет ViewFamilyType для плана этажа");

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();

        _transactions.RunInTransaction(Doc, "Seed views", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 3.0);
            _levelId = level.Id.GetValue();
            var plan = ModelSeed.CreateViewPlanChecked(doc, level.Id, "SC_FH_Plan");
            _viewId = plan.Id.GetValue();
            _planSeeded = true;
        });

        if (!_planSeeded)
            Skip.Test("Не удалось создать план в этом шаблоне проекта");
    }

    [After(Test)]
    public void Close()
    {
        Doc.Close(false);
    }

    private View GetView(long id) =>
        Doc.GetElement(ElementIdCompat.Create(id)) as View
        ?? throw new InvalidOperationException("Вид " + id + " не найден");

    private static ToolResult Run(AgentWriteToolBase tool, string json) =>
        tool.ExecuteForTests(Parse(json), approved: true, CancellationToken.None);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement Data(ToolResult result) =>
        result.Data ?? throw new InvalidOperationException("Tool result has no data.");

    private string FilterJson(string name, string? visibility, bool dryRun) =>
        "{\"viewIds\":[" + _viewId + "],\"name\":\"" + name + "\",\"categories\":[\"OST_Walls\"]," +
        "\"conditions\":[{\"parameter\":\"ALL_MODEL_INSTANCE_COMMENTS\",\"op\":\"eq\"," +
        "\"value\":\"SC_FH_X\"}]" +
        (visibility is null ? string.Empty : ",\"visibility\":\"" + visibility + "\"") +
        ",\"dryRun\":" + (dryRun ? "true" : "false") + "}";

    /// <summary>
    /// C4-09: visibility defaults to none — the filter is applied but disabled;
    /// the result must say so instead of looking like the elements are filtered.
    /// </summary>
    [Test]
    public async Task CreateViewFilter_DefaultVisibilityNone_WarnsAppliedButDisabled()
    {
        var tool = new CreateViewFilterTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var result = Run(tool, FilterJson("SC_FH_Disabled", null, dryRun: false));

        await Assert.That(result.Success).IsTrue();

        var data = Data(result);
        await Assert.That(data.GetProperty("appliedButDisabled").GetBoolean()).IsTrue();

        var warned = result.Warnings.Any(w =>
            w.Contains("DISABLED", StringComparison.Ordinal)
            && w.Contains("visibility", StringComparison.Ordinal));
        await Assert.That(warned).IsTrue();

        // The filter exists and is applied, but disabled: nothing is hidden.
        var filterId = data.GetProperty("filterId").GetInt64();
        var view = GetView(_viewId);
        await Assert.That(view.GetFilters().Any(id => id.GetValue() == filterId)).IsTrue();
    }

    /// <summary>
    /// C4-09 (control): visibility=hide produces no disabled warning.
    /// </summary>
    [Test]
    public async Task CreateViewFilter_VisibilityHide_NoDisabledWarning()
    {
        var tool = new CreateViewFilterTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var result = Run(tool, FilterJson("SC_FH_Hide", "hide", dryRun: false));

        await Assert.That(result.Success).IsTrue();
        await Assert.That(Data(result).TryGetProperty("appliedButDisabled", out _)).IsFalse();
        await Assert.That(result.Warnings.Any(w =>
            w.Contains("DISABLED", StringComparison.Ordinal))).IsFalse();
    }

    /// <summary>
    /// C4-16: the dry run and the apply must report the same number of changed[]
    /// rows for the same request, so expectedChanges can be set for a plan step.
    /// </summary>
    [Test]
    public async Task CreateViewFilter_DryRun_ChangedRowCountMatchesApply()
    {
        var tool = new CreateViewFilterTool(new StubRevitContext(Doc), _transactions!, _changeSets!);

        var dry = Run(tool, FilterJson("SC_FH_Parity", "hide", dryRun: true));
        await Assert.That(dry.Success).IsTrue();
        var dryCount = Data(dry).GetProperty("changed").GetArrayLength();

        var apply = Run(tool, FilterJson("SC_FH_Parity", "hide", dryRun: false));
        await Assert.That(apply.Success).IsTrue();
        var applyCount = Data(apply).GetProperty("changed").GetArrayLength();

        await Assert.That(dryCount).IsEqualTo(applyCount);
        await Assert.That(applyCount).IsEqualTo(2);
    }

    /// <summary>
    /// C4-17: dry-run ids are preview ids (readBack[].previewFilterId,
    /// filterIdPreview), apply ids are real; every row says what it did
    /// (action=filter_created / filter_assigned) instead of reading as a rename.
    /// </summary>
    [Test]
    public async Task CreateViewFilter_PreviewIdsAndRowActions_AreExplicit()
    {
        var tool = new CreateViewFilterTool(new StubRevitContext(Doc), _transactions!, _changeSets!);

        var dry = Run(tool, FilterJson("SC_FH_Preview", "hide", dryRun: true));
        await Assert.That(dry.Success).IsTrue();
        var dryData = Data(dry);
        await Assert.That(dryData.GetProperty("filterIdPreview").GetBoolean()).IsTrue();
        // Null fields of nested objects are not serialized (AgentJson drops them):
        // a missing key IS the null here.
        await Assert.That(dryData.GetProperty("readBack")[0]
                .GetProperty("previewFilterId").GetInt64())
            .IsGreaterThan(0);
        var dryFilterIdAbsent = !dryData.GetProperty("readBack")[0]
                .TryGetProperty("filterId", out var dryFilterId)
            || dryFilterId.ValueKind == JsonValueKind.Null;
        await Assert.That(dryFilterIdAbsent).IsTrue();
        await Assert.That(dryData.TryGetProperty("discardedProbeId", out _)).IsFalse();
        var dryActions = dryData.GetProperty("changed").EnumerateArray()
            .Where(row => row.TryGetProperty("action", out _))
            .Select(row => row.GetProperty("action").GetString())
            .OrderBy(action => action)
            .ToList();
        await Assert.That(string.Join(",", dryActions)).IsEqualTo("filter_assigned,filter_created");

        var apply = Run(tool, FilterJson("SC_FH_Preview", "hide", dryRun: false));
        await Assert.That(apply.Success).IsTrue();
        var applyData = Data(apply);
        await Assert.That(applyData.GetProperty("filterId").GetInt64()).IsGreaterThan(0);
        await Assert.That(applyData.TryGetProperty("filterIdPreview", out _)).IsFalse();
        await Assert.That(applyData.GetProperty("readBack")[0]
                .GetProperty("filterId").GetInt64())
            .IsGreaterThan(0);
        var previewAbsent = applyData.GetProperty("readBack")[0]
            .TryGetProperty("previewFilterId", out _);
        await Assert.That(previewAbsent).IsFalse();
        var applyActions = applyData.GetProperty("changed").EnumerateArray()
            .Where(row => row.TryGetProperty("action", out _))
            .Select(row => row.GetProperty("action").GetString())
            .OrderBy(action => action)
            .ToList();
        await Assert.That(string.Join(",", applyActions)).IsEqualTo("filter_assigned,filter_created");
    }

    /// <summary>
    /// DB-15: mirroring visibility from a source that shows categories the
    /// target had hidden must warn about the categories that became visible.
    /// </summary>
    [Test]
    public async Task CopyViewSettings_MirrorVisibility_WarnsAboutUnhiddenCategories()
    {
        Category? hidden = null;
        long targetId = 0;
        _transactions!.RunInTransaction(Doc, "Target view with a hidden category", doc =>
        {
            var target = ModelSeed.CreateViewPlanChecked(doc,
                ElementIdCompat.Create(_levelId), "SC_FH_Target");
            targetId = target.Id.GetValue();
            hidden = Category.GetCategory(doc, BuiltInCategory.OST_Casework);
            if (hidden is null)
                return;
            try
            {
                target.SetCategoryHidden(hidden.Id, true);
            }
            catch (Exception)
            {
                hidden = null;
            }
        });
        if (hidden is null)
            Skip.Test("Категория OST_Casework не скрывается на плане в этом шаблоне проекта");

        var tool = new CopyViewSettingsTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var result = Run(tool,
            "{\"sourceViewId\":" + _viewId + ",\"targetViewIds\":[" + targetId +
            "],\"sections\":[\"visibility\"],\"dryRun\":false}");

        await Assert.That(result.Success).IsTrue();

        var warned = result.Warnings.Any(w =>
            w.Contains("visible", StringComparison.Ordinal)
            && w.Contains(hidden!.Name, StringComparison.Ordinal));
        await Assert.That(warned).IsTrue();
    }

    /// <summary>
    /// N-ds-3: skipExisting=true leaves an existing view of that name untouched
    /// (skipped[]) instead of failing; duplicates inside the batch still fail.
    /// </summary>
    [Test]
    public async Task CreateViewsBatch_SkipExisting_SkipsInsteadOfFailing()
    {
        var tool = new CreateViewsBatchTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        var item =
            "{\"kind\":\"plan\",\"name\":\"SC_FH_Batch\",\"levelId\":" + _levelId + "}";

        var create = Run(tool, "{\"items\":[" + item + "],\"dryRun\":false}");
        await Assert.That(create.Success).IsTrue();

        var skip = Run(tool,
            "{\"items\":[" + item + "],\"skipExisting\":true,\"dryRun\":false}");
        await Assert.That(skip.Success).IsTrue();
        var skipData = Data(skip);
        await Assert.That(skipData.GetProperty("created").GetArrayLength()).IsEqualTo(0);
        await Assert.That(skipData.GetProperty("skipped").GetArrayLength()).IsEqualTo(1);
        await Assert.That(skipData.GetProperty("skipped")[0].GetProperty("name").GetString())
            .IsEqualTo("SC_FH_Batch");

        // Without the flag the same request keeps failing with view_name_taken.
        var fail = Run(tool, "{\"items\":[" + item + "],\"dryRun\":false}");
        await Assert.That(fail.Success).IsFalse();
        await Assert.That(fail.ErrorCode).IsEqualTo("view_name_taken");

        // A duplicate INSIDE the batch (a name the document does not have yet)
        // fails with view_name_taken even with skipExisting=true; the non-atomic
        // partial batch stays a success with the failure in failed[].
        var dupItem =
            "{\"kind\":\"plan\",\"name\":\"SC_FH_Dup\",\"levelId\":" + _levelId + "}";
        var duplicate = Run(tool,
            "{\"items\":[" + dupItem + "," + dupItem + "],\"skipExisting\":true,\"dryRun\":false}");
        var dupData = Data(duplicate);
        await Assert.That(dupData.GetProperty("created").GetArrayLength()).IsEqualTo(1);
        await Assert.That(dupData.GetProperty("failed").GetArrayLength()).IsEqualTo(1);
        await Assert.That(dupData.GetProperty("failed")[0].GetProperty("errorCode").GetString())
            .IsEqualTo("view_name_taken");
    }

    /// <summary>
    /// C10-02: trace_system_chain from an element without connectors must not
    /// read as an intact connected run (startConnectorCount/startConnectorNote
    /// plus a warning).
    /// </summary>
    [Test]
    public async Task TraceSystemChain_StartWithoutConnectors_Warns()
    {
        ElementId? wallId = null;
        _transactions!.RunInTransaction(Doc, "Seed a wall", doc =>
            wallId = ModelSeed.TryCreateWallAt(doc, ElementIdCompat.Create(_levelId), 0.0)?.Id);
        if (wallId is null)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        var tool = new TraceSystemChainTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc, Parse("{\"elementId\":" + wallId!.GetValue() + "}"), CancellationToken.None);

        await Assert.That(result.Success).IsTrue();

        var data = Data(result);
        // The wall has no connector manager at all: the null int? is not
        // serialized (AgentJson drops nulls), the note and the warning carry it.
        var countAbsent = !data.TryGetProperty("startConnectorCount", out var count)
                          || count.ValueKind == JsonValueKind.Null;
        await Assert.That(countAbsent).IsTrue();
        await Assert.That(string.IsNullOrEmpty(
                data.GetProperty("startConnectorNote").GetString()))
            .IsFalse();

        var warned = result.Warnings.Any(w =>
            w.Contains("no connectors", StringComparison.Ordinal));
        await Assert.That(warned).IsTrue();
        await Assert.That(result.Message!.Contains("WARNING", StringComparison.Ordinal)).IsTrue();
    }

    /// <summary>
    /// C10-02: get_element_relations explains an empty connectors list.
    /// </summary>
    [Test]
    public async Task GetElementRelations_EmptyConnectors_AreExplained()
    {
        ElementId? wallId = null;
        _transactions!.RunInTransaction(Doc, "Seed a wall", doc =>
            wallId = ModelSeed.TryCreateWallAt(doc, ElementIdCompat.Create(_levelId), 0.0)?.Id);
        if (wallId is null)
            Skip.Test("В шаблоне проекта по умолчанию нет типа стены");

        var tool = new GetElementRelationsTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"elementIds\":[" + wallId!.GetValue() + "]}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();

        var relation = Data(result).GetProperty("relations")[0];
        await Assert.That(relation.GetProperty("connectors").GetArrayLength()).IsEqualTo(0);
        await Assert.That(string.IsNullOrEmpty(
                relation.GetProperty("connectorsNote").GetString()))
            .IsFalse();
    }
}
