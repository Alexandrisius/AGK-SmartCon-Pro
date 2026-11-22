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
/// Batch view creation tools (real-cases v2 wave 2): create_floor_plans
/// (one transaction per batch, name pattern with floor/level placeholders,
/// ViewTemplateId assignment, in-batch duplicate guard, dry run, undo) and
/// create_views_batch (mixed plan/3d items with optional template and scale).
/// </summary>
public sealed class AgentBatchViewToolsTests : RevitApiTest
{
    private const string Pattern = "SC_Этаж {floor}_Отопление";

    private Document? _document;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;
    private long _level1;
    private long _level2;
    private long _level3;
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

        _transactions.RunInTransaction(Doc, "Seed batch view tools", doc =>
        {
            _level1 = SeedLevel(doc, 0.0, 1);
            _level2 = SeedLevel(doc, 3.0, 2);
            _level3 = SeedLevel(doc, 6.0, 3);

            var plan = ModelSeed.CreateViewPlanChecked(doc, ElementIdCompat.Create(_level1), "SC_Seed_Plan");
            var template = plan.CreateViewTemplate();
            if (template is not null)
            {
                template.Name = "SC_Отопление";
                // Detach the scale from the template (SetViewTemplateInclusion
                // mechanics: the setter accepts only ids from the whitelist of
                // GetTemplateParameterIds; the scale is THREE parameters —
                // VIEW_SCALE + two *_PULLDOWN_*): a template created by
                // CreateViewTemplate controls the scale, which would make the
                // scale check of the batch tool non-deterministic.
                var scaleIds = new HashSet<long>
                {
                    new ElementId(BuiltInParameter.VIEW_SCALE).GetValue(),
                    new ElementId(BuiltInParameter.VIEW_SCALE_PULLDOWN_IMPERIAL).GetValue(),
                    new ElementId(BuiltInParameter.VIEW_SCALE_PULLDOWN_METRIC).GetValue()
                };
                var scaleParams = template.GetTemplateParameterIds()
                    .Where(id => scaleIds.Contains(id.GetValue()))
                    .ToList();
                template.SetNonControlledTemplateParameterIds(scaleParams);
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
    public async Task FloorPlans_DryRunCreatesNothing()
    {
        RequireTemplate();

        var dry = RunFloorPlans(
            "{\"levels\":[" + Levels() + "],\"templateId\":" + _templateId +
            ",\"namePattern\":\"" + Pattern + "\",\"dryRun\":true}");

        await Assert.That(dry.Success).IsTrue();
        await Assert.That(Data(dry).GetProperty("createdCount").GetInt32()).IsEqualTo(3);
        await Assert.That(FloorPlanCount(Pattern)).IsEqualTo(0);
    }

    [Test]
    public async Task FloorPlans_CreatesNamedTemplatedPlansAndUndoDeletesAll()
    {
        RequireTemplate();

        var apply = RunFloorPlans(
            "{\"levels\":[" + Levels() + "],\"templateId\":" + _templateId +
            ",\"namePattern\":\"" + Pattern + "\",\"dryRun\":false}");

        await Assert.That(apply.Success).IsTrue();
        await Assert.That(Data(apply).GetProperty("createdCount").GetInt32()).IsEqualTo(3);

        var created = ReadCreated(apply);
        await Assert.That(created.Count).IsEqualTo(3);

        var expectedNames = new[]
        {
            "SC_Этаж 1_Отопление", "SC_Этаж 2_Отопление", "SC_Этаж 3_Отопление"
        };
        foreach (var row in created)
        {
            var view = GetView(row.viewId);
            await Assert.That(view).IsTypeOf<ViewPlan>();
            await Assert.That(view.Name).IsEqualTo(row.name);
            await Assert.That(expectedNames).Contains(row.name);
            await Assert.That(view.ViewTemplateId.GetValue()).IsEqualTo(_templateId);
            await Assert.That(row.genLevelId).IsEqualTo(row.levelId);
        }

        var undo = RunUndo();
        await Assert.That(undo.Success).IsTrue();
        foreach (var row in created)
            await Assert.That(Doc.GetElement(ElementIdCompat.Create(row.viewId))).IsNull();
    }

    [Test]
    public async Task FloorPlans_ExplicitFloorsWinOverNameParsing()
    {
        RequireTemplate();

        var apply = RunFloorPlans(
            "{\"levels\":[" + _level2 + "],\"templateId\":" + _templateId +
            ",\"namePattern\":\"" + Pattern + "\",\"floors\":{\"" + _level2 + "\":\"7\"}," +
            "\"dryRun\":false}");

        await Assert.That(apply.Success).IsTrue();
        var created = ReadCreated(apply);
        await Assert.That(created.Count).IsEqualTo(1);
        await Assert.That(created[0].name).IsEqualTo("SC_Этаж 7_Отопление");
        await Assert.That(GetView(created[0].viewId).Name).IsEqualTo("SC_Этаж 7_Отопление");
    }

    [Test]
    public async Task FloorPlans_InBatchDuplicateNameFailsSecondItem()
    {
        RequireTemplate();

        // The pattern has no {floor}: both levels resolve to the same name —
        // the first item is created, the second one fails view_name_taken.
        var apply = RunFloorPlans(
            "{\"levels\":[" + _level1 + "," + _level2 + "],\"templateId\":" + _templateId +
            ",\"namePattern\":\"SC_БезПодстановки\",\"atomic\":false,\"dryRun\":false}");

        await Assert.That(apply.Success).IsTrue();
        await Assert.That(Data(apply).GetProperty("createdCount").GetInt32()).IsEqualTo(1);

        var failed = Data(apply).GetProperty("failed");
        await Assert.That(failed.GetArrayLength()).IsEqualTo(1);
        await Assert.That(failed[0].GetProperty("errorCode").GetString())
            .IsEqualTo("view_name_taken");
        await Assert.That(GetView(ReadCreated(apply)[0].viewId).Name)
            .IsEqualTo("SC_БезПодстановки");
    }

    [Test]
    public async Task FloorPlans_Guards_LevelNotFoundAndTemplateMismatch()
    {
        RequireTemplate();

        var noLevel = RunFloorPlans(
            "{\"levels\":[987654321],\"templateId\":" + _templateId +
            ",\"namePattern\":\"" + Pattern + "\",\"atomic\":false,\"dryRun\":false}");
        await Assert.That(noLevel.Success).IsFalse();
        await Assert.That(Data(noLevel).GetProperty("failed")[0]
            .GetProperty("errorCode").GetString()).IsEqualTo("level_not_found");
        await Assert.That(FloorPlanCount(Pattern)).IsEqualTo(0);

        var template3D = TryCreate3DTemplate();
        if (template3D is null)
        {
            Skip.Test("В шаблоне проекта нет ViewFamilyType для 3D-вида");
            return;
        }

        var mismatch = RunFloorPlans(
            "{\"levels\":[" + Levels() + "],\"templateId\":" + template3D +
            ",\"namePattern\":\"" + Pattern + "\",\"atomic\":true,\"dryRun\":false}");
        await Assert.That(mismatch.Success).IsFalse();
        await Assert.That(mismatch.ErrorCode).IsEqualTo("template_view_type_mismatch");
        await Assert.That(FloorPlanCount(Pattern)).IsEqualTo(0);
    }

    [Test]
    public async Task Batch_CreatesPlanAnd3DWithTemplateScaleAndUndoDeletesBoth()
    {
        RequireTemplate();
        if (!Has3DViewType())
            Skip.Test("В шаблоне проекта нет ViewFamilyType для 3D-вида");

        var apply = RunBatch(
            "{\"items\":[" +
            "{\"kind\":\"plan\",\"name\":\"SC_Batch_Plan\",\"levelId\":" + _level3 +
            ",\"templateId\":" + _templateId + ",\"scale\":200}," +
            "{\"kind\":\"3d\",\"name\":\"SC_Batch_3D\"}" +
            "],\"atomic\":true,\"dryRun\":false}");

        await Assert.That(apply.Success).IsTrue();
        var created = ReadCreated(apply);
        await Assert.That(created.Count).IsEqualTo(2);

        var plan = GetView(created.Single(row => row.kind == "plan").viewId);
        await Assert.That(plan).IsTypeOf<ViewPlan>();
        await Assert.That(plan.Name).IsEqualTo("SC_Batch_Plan");
        await Assert.That(plan.ViewTemplateId.GetValue()).IsEqualTo(_templateId);
        await Assert.That(plan.Scale).IsEqualTo(200);

        var view3D = GetView(created.Single(row => row.kind == "3d").viewId);
        await Assert.That(view3D).IsTypeOf<View3D>();
        await Assert.That(view3D.Name).IsEqualTo("SC_Batch_3D");

        var undo = RunUndo();
        await Assert.That(undo.Success).IsTrue();
        foreach (var row in created)
            await Assert.That(Doc.GetElement(ElementIdCompat.Create(row.viewId))).IsNull();
    }

    [Test]
    public async Task Batch_DuplicateNamesAndMissingLevel_Guarded()
    {
        RequireTemplate();
        if (!Has3DViewType())
            Skip.Test("В шаблоне проекта нет ViewFamilyType для 3D-вида");

        var duplicates = RunBatch(
            "{\"items\":[" +
            "{\"kind\":\"3d\",\"name\":\"SC_Batch_Dup\"}," +
            "{\"kind\":\"3d\",\"name\":\"SC_Batch_Dup\"}" +
            "],\"atomic\":false,\"dryRun\":false}");

        await Assert.That(Data(duplicates).GetProperty("failed").GetArrayLength()).IsEqualTo(1);
        await Assert.That(Data(duplicates).GetProperty("failed")[0]
            .GetProperty("errorCode").GetString()).IsEqualTo("view_name_taken");

        var noLevel = RunBatch(
            "{\"items\":[{\"kind\":\"plan\",\"name\":\"SC_Batch_No\",\"levelId\":987654321}]," +
            "\"atomic\":false,\"dryRun\":false}");
        await Assert.That(noLevel.Success).IsFalse();
        await Assert.That(Data(noLevel).GetProperty("failed")[0]
            .GetProperty("errorCode").GetString()).IsEqualTo("level_not_found");

        var noKindLevel = RunBatch(
            "{\"items\":[{\"kind\":\"plan\",\"name\":\"SC_Batch_No2\"}],\"dryRun\":true}");
        await Assert.That(Data(noKindLevel).GetProperty("failed").GetArrayLength()).IsEqualTo(1);
    }

    private static long SeedLevel(Document doc, double elevation, int number)
    {
        var level = ModelSeed.CreateLevel(doc, elevation);
        level.Name = "SC_Уровень " + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return level.Id.GetValue();
    }

    private string Levels() => _level1 + "," + _level2 + "," + _level3;

    private long? TryCreate3DTemplate()
    {
        long? id = null;
        _transactions!.RunInTransaction(Doc, "Seed 3D template", doc =>
        {
            var familyType = FirstFamilyType(doc, ViewFamily.ThreeDimensional);
            if (familyType is null)
                return;

            var view3D = View3D.CreateIsometric(doc, familyType.Id);
            var template = view3D.CreateViewTemplate();
            if (template is not null)
            {
                template.Name = "SC_3D";
                id = template.Id.GetValue();
            }
        });
        return id;
    }

    private bool Has3DViewType() =>
        FirstFamilyType(Doc, ViewFamily.ThreeDimensional) is not null;

    private static ViewFamilyType? FirstFamilyType(Document doc, ViewFamily family)
    {
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)))
        {
            if (element is ViewFamilyType candidate && candidate.ViewFamily == family)
                return candidate;
        }

        return null;
    }

    private int FloorPlanCount(string nameContains)
    {
        return new FilteredElementCollector(Doc)
            .OfClass(typeof(ViewPlan))
            .Cast<View>()
            .Count(view => !view.IsTemplate &&
                           view.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private View GetView(long viewId) =>
        Doc.GetElement(ElementIdCompat.Create(viewId)) as View
        ?? throw new InvalidOperationException("Вид " + viewId + " не найден");

    private static List<(long viewId, long levelId, string name, string kind, long? genLevelId)>
        ReadCreated(ToolResult result)
    {
        var rows = new List<(long, long, string, string, long?)>();
        foreach (var element in Data(result).GetProperty("created").EnumerateArray())
        {
            rows.Add((
                element.GetProperty("viewId").GetInt64(),
                element.TryGetProperty("levelId", out var levelId) &&
                levelId.ValueKind == JsonValueKind.Number
                    ? levelId.GetInt64()
                    : 0,
                element.GetProperty("name").GetString()!,
                element.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String
                    ? kind.GetString()!
                    : "plan",
                element.TryGetProperty("genLevelId", out var genLevel) &&
                genLevel.ValueKind == JsonValueKind.Number
                    ? genLevel.GetInt64()
                    : null));
        }

        return rows;
    }

    private ToolResult RunFloorPlans(string json) =>
        Run(new CreateFloorPlansTool(new StubRevitContext(Doc), _transactions!, _changeSets!), json);

    private ToolResult RunBatch(string json) =>
        Run(new CreateViewsBatchTool(new StubRevitContext(Doc), _transactions!, _changeSets!), json);

    private ToolResult RunUndo() =>
        Run(new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!),
            "{\"dryRun\":false}");

    private static ToolResult Run(AgentWriteToolBase tool, string json) =>
        tool.ExecuteForTests(Parse(json), approved: true, CancellationToken.None);

    private void RequireTemplate()
    {
        if (_templateId == 0)
            Skip.Test("Шаблон вида не создан");
    }

    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement Data(ToolResult result) =>
        result.Data ?? throw new InvalidOperationException("Tool result has no data.");
}
