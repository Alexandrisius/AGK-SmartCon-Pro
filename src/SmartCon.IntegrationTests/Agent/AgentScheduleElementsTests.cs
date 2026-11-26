using System.Text.Json;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Compatibility;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Transactions;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// get_schedule_elements (the element ids behind schedule rows) and the scope
/// verdict of get_schedule_definition for a type-bound shared parameter.
/// </summary>
public sealed class AgentScheduleElementsTests : RevitApiTest
{
    private const string MarkValue = "SC_SchedEl";

    private Document? _document;
    private RevitTransactionService? _transactions;

    private Document Doc => _document!;

    private RevitTransactionService Transactions => _transactions!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasFloorPlanViewType(Doc))
            Skip.Test("В шаблоне проекта нет ViewFamilyType для плана этажа");

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
    }

    [After(Test)]
    public void Close()
    {
        Doc.Close(false);
    }

    [Test]
    public async Task GetScheduleElements_NoArguments_IsInvalidArguments()
    {
        var tool = new GetScheduleElementsTool(new StubRevitContext(Doc));

        var result = tool.ExecuteForTests(Doc, Parse("{}"), CancellationToken.None);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("invalid_arguments");
    }

    [Test]
    public async Task GetScheduleElements_ReturnsFilteredRowsWithTypeIds()
    {
        var (scheduleId, expectedIds) = SeedWallSchedule(withFilter: true);
        if (scheduleId == 0 || expectedIds.Count == 0)
            Skip.Test("Не удалось посеять стены и спецификацию стен");

        var tool = new GetScheduleElementsTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"scheduleId\":" + scheduleId + ",\"limit\":100}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var rows = result.Data!.Value.GetProperty("rows").EnumerateArray().ToList();
        var instances = rows.Where(row => row.GetProperty("kind").GetString() == "instance")
            .ToList();
        var actualIds = instances.Select(row => row.GetProperty("elementId").GetInt64())
            .ToHashSet();

        // The schedule view applies its own filter: only the SC_SchedEl walls are
        // rows, and every row carries the real type id.
        await Assert.That(actualIds.SetEquals(expectedIds)).IsTrue();
        foreach (var row in instances)
        {
            var wall = (Wall)Doc.GetElement(ElementIdCompat.Create(
                row.GetProperty("elementId").GetInt64()))!;
            await Assert.That(row.GetProperty("typeId").GetInt64())
                .IsEqualTo(wall.GetTypeId().GetValue());
        }

        // S.45: the composition summary describes the whole set, not the page.
        await Assert.That(result.Data!.Value.GetProperty("uniqueTypes").GetInt32())
            .IsGreaterThanOrEqualTo(1);
        var categories = result.Data!.Value.GetProperty("byCategory").EnumerateArray().ToList();
        await Assert.That(categories.Sum(entry => entry.GetProperty("count").GetInt32()))
            .IsEqualTo(result.Data!.Value.GetProperty("total").GetInt32());

        // The row count agrees with what get_schedule_data reports for the same view.
        var data = new GetScheduleDataTool(new StubRevitContext(Doc)).ExecuteForTests(
            Doc, Parse("{\"scheduleId\":" + scheduleId + ",\"limit\":500}"),
            CancellationToken.None);
        await Assert.That(data.Success).IsTrue();
        await Assert.That(data.Data!.Value.GetProperty("totalRows").GetInt32())
            .IsEqualTo(instances.Count);
    }

    [Test]
    public async Task GetScheduleElements_ByType_ReturnsOneEntryPerType()
    {
        var (scheduleId, expectedIds) = SeedWallSchedule(withFilter: true);
        if (scheduleId == 0 || expectedIds.Count == 0)
            Skip.Test("Не удалось посеять стены и спецификацию стен");

        var tool = new GetScheduleElementsTool(new StubRevitContext(Doc));
        var rowsResult = tool.ExecuteForTests(
            Doc,
            Parse("{\"scheduleId\":" + scheduleId + ",\"limit\":500}"),
            CancellationToken.None);
        var byTypeResult = tool.ExecuteForTests(
            Doc,
            Parse("{\"scheduleId\":" + scheduleId + ",\"byType\":true}"),
            CancellationToken.None);

        await Assert.That(byTypeResult.Success).IsTrue();
        var types = byTypeResult.Data!.Value.GetProperty("types").EnumerateArray().ToList();
        var rows = rowsResult.Data!.Value.GetProperty("rows").EnumerateArray()
            .Where(row => row.GetProperty("kind").GetString() == "instance").ToList();

        // One entry per distinct typeId of the rows, instanceCount sums to the rows.
        var distinctTypeIds = rows
            .Where(row => row.TryGetProperty("typeId", out var id)
                          && id.ValueKind != JsonValueKind.Null)
            .Select(row => row.GetProperty("typeId").GetInt64())
            .Distinct()
            .ToHashSet();
        await Assert.That(types.Count).IsEqualTo(distinctTypeIds.Count);
        await Assert.That(types.Sum(entry => entry.GetProperty("instanceCount").GetInt32()))
            .IsEqualTo(rows.Count);
        foreach (var entry in types)
            await Assert.That(distinctTypeIds.Contains(entry.GetProperty("typeId").GetInt64()))
                .IsTrue();
        await Assert.That(byTypeResult.Data!.Value.GetProperty("total").GetInt32())
            .IsEqualTo(rows.Count);
    }

    [Test]
    public async Task CurrentViewScheduleBlock_DescribesScheduleWithoutGraphicalProperties()
    {
        long scheduleId = 0;
        Transactions.RunInTransaction(Doc, "Seed current view schedule", doc =>
        {
            var schedule = ViewSchedule.CreateSchedule(doc, new ElementId(BuiltInCategory.OST_Walls));
            schedule.Name = "SC_CurrentViewSchedule";
            scheduleId = schedule.Id.GetValue();
        });
        if (scheduleId == 0)
            Skip.Test("Revit не создал спецификацию стен");

        var schedule = (ViewSchedule)Doc.GetElement(ElementIdCompat.Create(scheduleId))!;
        var block = GetCurrentViewInfoTool.DescribeSchedule(schedule);

        await Assert.That((long)block["scheduleId"]!).IsEqualTo(scheduleId);
        await Assert.That((bool)block["isMulticategory"]!).IsFalse();
        await Assert.That((int)block["rowCount"]!).IsGreaterThanOrEqualTo(0);
        await Assert.That((string)block["hint"]!).Contains("get_schedule_elements");
    }

    [Test]
    public async Task GetScheduleElements_IncludeTypesFalse_KeepsInstancesOnly()
    {
        var (scheduleId, _) = SeedWallSchedule(withFilter: false);
        if (scheduleId == 0)
            Skip.Test("Не удалось посеять спецификацию стен");

        var tool = new GetScheduleElementsTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc,
            Parse("{\"scheduleId\":" + scheduleId + ",\"includeTypes\":false}"),
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        foreach (var row in result.Data!.Value.GetProperty("rows").EnumerateArray())
            await Assert.That(row.GetProperty("kind").GetString()).IsEqualTo("instance");
    }

    [Test]
    public async Task GetScheduleElements_NonItemizedSchedule_ReturnsInstancesWithTypeIds()
    {
        var (scheduleId, wallId, wallTypeId) = SeedWallTypeSchedule();
        if (scheduleId == 0)
            Skip.Test("Не удалось посеять нейтемизированную спецификацию стен");

        var tool = new GetScheduleElementsTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc, Parse("{\"scheduleId\":" + scheduleId + "}"), CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var rows = result.Data!.Value.GetProperty("rows").EnumerateArray().ToList();
        await Assert.That(rows.Count).IsGreaterThanOrEqualTo(1);
        // Revit keeps a grouped (non-itemized) schedule instance-based: the
        // collector returns the instances behind the grouped rows.
        foreach (var row in rows)
        {
            await Assert.That(row.GetProperty("kind").GetString()).IsEqualTo("instance");
            await Assert.That(row.GetProperty("typeId").GetInt64()).IsEqualTo(wallTypeId);
        }

        await Assert.That(rows.Any(row => row.GetProperty("elementId").GetInt64() == wallId))
            .IsTrue();
    }

    [Test]
    public async Task GetScheduleElements_MulticategorySchedule_IsReportedAndReadable()
    {
        long scheduleId = 0;
        Transactions.RunInTransaction(Doc, "Seed multicategory schedule", doc =>
        {
            var schedule = ViewSchedule.CreateSchedule(doc, ElementId.InvalidElementId);
            schedule.Name = "SC_MulticategorySchedule";
            scheduleId = schedule.Id.GetValue();
        });
        if (scheduleId == 0)
            Skip.Test("Revit не создал мультикатегорийную спецификацию");

        var tool = new GetScheduleElementsTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc, Parse("{\"scheduleId\":" + scheduleId + "}"), CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Data!.Value.GetProperty("isMulticategory").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task GetScheduleDefinition_TypeBoundSharedParameter_ReportsTypeScope()
    {
        var (wallId, levelId) = SeedWall(out var wallTypeId);
        using var sharedFile = SharedParameterFileScope.Create(Application);
        var parameterGuid = Guid.Parse("B5C2C26A-4C62-4E1B-8C7A-1A2B3C4D5E6F");
        const string parameterName = "SC_ScopeParam";

        Transactions.RunInTransaction(Doc, "Bind type parameter", doc =>
        {
            var file = Application.OpenSharedParameterFile();
            var group = file!.Groups.Create("SC_AgentScheduleScope");
            var definition = (ExternalDefinition)group.Definitions.Create(
                new ExternalDefinitionCreationOptions(parameterName, SpecTypeId.String.Text)
                {
                    GUID = parameterGuid
                });
            var categories = Application.Create.NewCategorySet();
            categories.Insert(Category.GetCategory(doc, BuiltInCategory.OST_Walls)!);
            var binding = Application.Create.NewTypeBinding(categories);
            doc.ParameterBindings.Insert(definition, binding);

            var wallType = (ElementType)doc.GetElement(ElementIdCompat.Create(wallTypeId));
            wallType.get_Parameter(parameterGuid)?.Set("type-value");
        });
        _ = wallId;
        _ = levelId;

        long scheduleId = 0;
        Transactions.RunInTransaction(Doc, "Seed scope schedule", doc =>
        {
            var schedule = ViewSchedule.CreateSchedule(doc, new ElementId(BuiltInCategory.OST_Walls));
            schedule.Name = "SC_ScopeSchedule";
            scheduleId = schedule.Id.GetValue();
            foreach (var field in schedule.Definition.GetSchedulableFields())
            {
                if (string.Equals(field.GetName(Doc), parameterName, StringComparison.Ordinal))
                {
                    schedule.Definition.AddField(field);
                    break;
                }
            }
        });
        if (scheduleId == 0)
            Skip.Test("Revit не создал спецификацию стен");

        var tool = new GetScheduleDefinitionTool(new StubRevitContext(Doc));
        var result = tool.ExecuteForTests(
            Doc, Parse("{\"scheduleId\":" + scheduleId + "}"), CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var field = result.Data!.Value.GetProperty("fields").EnumerateArray()
            .FirstOrDefault(item => item.GetProperty("name").GetString() == parameterName);
        await Assert.That(field.ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(field.GetProperty("scope").GetString()).IsEqualTo("type");
        await Assert.That(field.GetProperty("scopeSource").GetString()).IsNotNull();
        await Assert.That(field.TryGetProperty("scopeConflict", out _)).IsFalse();
    }

    private (long ScheduleId, HashSet<long> WallIds) SeedWallSchedule(bool withFilter)
    {
        var (wallId, levelId) = SeedWall(out _);
        var wallIds = new HashSet<long>();
        long scheduleId = 0;

        Transactions.RunInTransaction(Doc, "Seed wall schedule", doc =>
        {
            var wall = (Wall)doc.GetElement(ElementIdCompat.Create(wallId))!;
            wall.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(MarkValue);
            wallIds.Add(wallId);

            var second = ModelSeed.TryCreateWall(doc, ElementIdCompat.Create(levelId));
            if (second is not null)
            {
                second.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set("SC_Other");
                wall.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set(MarkValue);
            }

            var schedule = ViewSchedule.CreateSchedule(doc, new ElementId(BuiltInCategory.OST_Walls));
            schedule.Name = "SC_ElementsSchedule";
            scheduleId = schedule.Id.GetValue();

            ScheduleField? markField = null;
            foreach (var field in schedule.Definition.GetSchedulableFields())
            {
                if (field.ParameterId?.GetValue() == (long)(int)BuiltInParameter.ALL_MODEL_MARK)
                {
                    markField = schedule.Definition.AddField(field);
                    break;
                }
            }

            if (withFilter && markField is not null)
            {
                schedule.Definition.AddFilter(new ScheduleFilter(
                    markField.FieldId, ScheduleFilterType.Equal, MarkValue));
            }
        });

        return (scheduleId, wallIds);
    }

    private (long ScheduleId, long WallId, long WallTypeId) SeedWallTypeSchedule()
    {
        var (wallId, _) = SeedWall(out var wallTypeId);
        long scheduleId = 0;
        Transactions.RunInTransaction(Doc, "Seed non-itemized schedule", doc =>
        {
            var schedule = ViewSchedule.CreateSchedule(doc, new ElementId(BuiltInCategory.OST_Walls));
            schedule.Name = "SC_TypeSchedule";
            schedule.Definition.IsItemized = false;
            foreach (var field in schedule.Definition.GetSchedulableFields())
            {
                if (field.ParameterId?.GetValue() == (long)(int)BuiltInParameter.ALL_MODEL_MARK)
                {
                    schedule.Definition.AddField(field);
                    break;
                }
            }

            scheduleId = schedule.Id.GetValue();
        });

        return (scheduleId, wallId, wallTypeId);
    }

    private (long WallId, long LevelId) SeedWall(out long wallTypeId)
    {
        long wallId = 0;
        long levelId = 0;
        long typeId = 0;
        Transactions.RunInTransaction(Doc, "Seed walls", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 3.0);
            levelId = level.Id.GetValue();
            var wall = ModelSeed.TryCreateWall(doc, level.Id);
            if (wall is not null)
            {
                wallId = wall.Id.GetValue();
                typeId = wall.GetTypeId().GetValue();
            }
        });

        wallTypeId = typeId;
        return (wallId, levelId);
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;
}
