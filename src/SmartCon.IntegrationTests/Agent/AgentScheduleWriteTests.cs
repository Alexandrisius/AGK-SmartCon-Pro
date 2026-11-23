using System.Globalization;
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

public sealed class AgentScheduleWriteTests : RevitApiTest
{
    private const string LevelName = "SC_SchWrite_Level";
    private const string ScheduleName = "SC_SchWrite_Walls";
    private const string SheetNumber = "SC-SW-100";
    private const string WallComment = "SC_SchWrite_Wall";

    private Document? _document;
    private RevitTransactionService? _transactions;
    private InMemoryAgentChangeSetStore? _changeSets;
    private bool _sheetCreated;
    private long _sheetId;
    private long _levelId;
    private int _wallCount;
    private string _commentsField = "Comments";
    private string _markField = "Mark";

    private Document Doc => _document!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        if (!ModelSeed.HasFloorPlanViewType(Doc))
            Skip.Test("В шаблоне проекта по умолчанию нет ViewFamilyType для плана этажа");

        _transactions = new RevitTransactionService(new StubRevitContext(Doc));
        _changeSets = new InMemoryAgentChangeSetStore();

        _transactions.RunInTransaction(Doc, "Seed schedule write model", doc =>
        {
            var level = ModelSeed.CreateLevel(doc, 0.0);
            level.Name = LevelName;
            _levelId = level.Id.GetValue();

            for (var index = 0; index < 3; index++)
            {
                var wall = ModelSeed.TryCreateWallAt(doc, level.Id, index * 3.0);
                if (wall is null)
                    continue;

                _wallCount++;
                wall.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                    ?.Set(WallComment);
                wall.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)
                    ?.Set("M" + index.ToString(CultureInfo.InvariantCulture));
            }

            var probe = ModelSeed.CreateScheduleChecked(doc);
            foreach (var field in probe.Definition.GetSchedulableFields())
            {
                if (field.ParameterId == new ElementId(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS))
                    _commentsField = field.GetName(doc);
                if (field.ParameterId == new ElementId(BuiltInParameter.ALL_MODEL_MARK))
                    _markField = field.GetName(doc);
            }

            doc.Delete(probe.Id);

            var titleBlockId = ModelSeed.FindTitleBlockTypeId(doc);
            if (titleBlockId is null)
            {
                var titleBlockPath = ModelSeed.FindTitleBlockFamilyFile(
                    doc.Application.VersionNumber);
                if (titleBlockPath is not null)
                {
                    doc.LoadFamily(titleBlockPath, out _);
                    titleBlockId = ModelSeed.FindTitleBlockTypeId(doc);
                }
            }

            if (titleBlockId is null)
                return;

            var sheet = ViewSheet.Create(doc, titleBlockId);
            sheet.SheetNumber = SheetNumber;
            sheet.Name = "SC_SchWrite_Sheet";
            _sheetId = sheet.Id.GetValue();
            _sheetCreated = true;
        });
    }

    [After(Test)]
    public void Close()
    {
        Doc.Close(false);
    }

    [Test]
    public async Task CreateSchedule_DryRun_NoChanges_ThenApply_ReadsBack()
    {
        var tool = CreateTool<CreateScheduleTool>();
        var dry = tool.ExecuteForTests(
            Parse("{\"category\":\"OST_Walls\",\"name\":\"" + ScheduleName +
                  "\",\"fields\":[\"" + _markField + "\",\"" + _commentsField +
                  "\"],\"dryRun\":true}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(dry.Success).IsTrue();
        await Assert.That(FindSchedule(ScheduleName)).IsNull();
        await Assert.That(_changeSets!.All.Count).IsEqualTo(0);

        var apply = tool.ExecuteForTests(
            Parse("{\"category\":\"OST_Walls\",\"name\":\"" + ScheduleName +
                  "\",\"fields\":[\"" + _markField + "\",\"" + _commentsField +
                  "\"],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(apply.Success).IsTrue();
        var schedule = FindSchedule(ScheduleName);
        await Assert.That(schedule).IsNotNull();

        var reader = new GetScheduleDefinitionTool(new StubRevitContext(Doc));
        var definition = reader.ExecuteForTests(
            Doc,
            Parse("{\"scheduleName\":\"" + ScheduleName + "\"}"),
            CancellationToken.None);

        await Assert.That(definition.Success).IsTrue();
        var data = definition.Data!.Value;
        await Assert.That(data.GetProperty("name").GetString()).IsEqualTo(ScheduleName);
        await Assert.That(data.GetProperty("categoryName").GetString()).IsEqualTo("Walls");
        await Assert.That(data.GetProperty("isItemized").GetBoolean()).IsTrue();
        await Assert.That(data.GetProperty("fieldCount").GetInt32()).IsEqualTo(2);
        var names = data.GetProperty("fields").EnumerateArray()
            .Select(field => field.GetProperty("name").GetString())
            .ToList();
        await Assert.That(names.Contains(_markField)).IsTrue();
        await Assert.That(names.Contains(_commentsField)).IsTrue();

        var set = _changeSets!.Last!;
        await Assert.That(set.Header.Operation).IsEqualTo("create_schedule");
        await Assert.That(set.Items[0].ParameterName).IsEqualTo("schedule_created");
    }

    [Test]
    public async Task CreateSchedule_DuplicateName_TypedConflict()
    {
        var tool = CreateTool<CreateScheduleTool>();
        var first = tool.ExecuteForTests(
            Parse("{\"category\":\"OST_Walls\",\"name\":\"" + ScheduleName + "\",\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(first.Success).IsTrue();

        var second = tool.ExecuteForTests(
            Parse("{\"category\":\"OST_Walls\",\"name\":\"" + ScheduleName + "\",\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(second.Success).IsFalse();
        await Assert.That(second.ErrorCode).IsEqualTo("schedule_name_taken");
    }

    [Test]
    public async Task CreateSchedule_UnknownField_TypedErrorWithAvailableFields()
    {
        var tool = CreateTool<CreateScheduleTool>();
        var result = tool.ExecuteForTests(
            Parse("{\"category\":\"OST_Walls\",\"name\":\"SC_SchWrite_Bad\",\"fields\":[\"Нет такого поля XYZ\"],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var failed = result.Data!.Value.GetProperty("failed");
        await Assert.That(failed.GetArrayLength()).IsEqualTo(1);
        await Assert.That(failed[0].GetProperty("errorCode").GetString())
            .IsEqualTo("field_not_available");
        var available = result.Data.Value.GetProperty("availableFields").EnumerateArray().ToList();
        await Assert.That(available.Count).IsGreaterThan(0);

        var schedule = FindSchedule("SC_SchWrite_Bad");
        await Assert.That(schedule).IsNotNull();
        await Assert.That(schedule!.Definition.GetFieldCount()).IsEqualTo(0);
    }

    [Test]
    public async Task AddScheduleField_Applies_ReadsBack_AndUndoRemovesField()
    {
        var created = CreateWallSchedule();
        var tool = CreateTool<AddScheduleFieldTool>();
        var result = tool.ExecuteForTests(
            Parse("{\"scheduleId\":" + created + ",\"fields\":[\"" + _commentsField +
                  "\"],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var fields = result.Data!.Value.GetProperty("fields").EnumerateArray()
            .Select(value => value.GetString())
            .ToList();
        await Assert.That(fields.Contains(_commentsField)).IsTrue();

        var second = tool.ExecuteForTests(
            Parse("{\"scheduleId\":" + created + ",\"fields\":[\"" + _commentsField +
                  "\"],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(second.Success).IsTrue();
        await Assert.That(second.Data!.Value.GetProperty("noChanges").GetBoolean()).IsTrue();

        var undo = UndoLast();
        await Assert.That(undo.Success).IsTrue();
        var schedule = (ViewSchedule)Doc.GetElement(ElementIdCompat.Create(created))!;
        var remaining = AgentScheduleReadFieldNames(schedule);
        await Assert.That(remaining.Contains(_commentsField)).IsFalse();
    }

    [Test]
    public async Task SetScheduleFilter_Applies_ReadsBack_FieldNotInSchedule_AndLimit()
    {
        var created = CreateWallSchedule();
        var tool = CreateTool<SetScheduleFilterTool>();

        var notInSchedule = tool.ExecuteForTests(
            Parse("{\"scheduleId\":" + created + ",\"filters\":[{\"field\":\"" + _commentsField +
                  "\",\"op\":\"equals\",\"value\":\"x\"}],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(notInSchedule.Success).IsFalse();
        await Assert.That(notInSchedule.ErrorCode).IsEqualTo("field_not_in_schedule");

        var nine = new List<string>();
        for (var index = 0; index < 9; index++)
            nine.Add("{\"field\":\"f" + index + "\",\"op\":\"equals\",\"value\":\"x\"}");
        var overLimit = tool.ExecuteForTests(
            Parse("{\"scheduleId\":" + created + ",\"filters\":[" + string.Join(",", nine) +
                  "],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(overLimit.Success).IsFalse();
        await Assert.That(overLimit.ErrorCode).IsEqualTo("filter_limit_exceeded");

        var addField = CreateTool<AddScheduleFieldTool>();
        var added = addField.ExecuteForTests(
            Parse("{\"scheduleId\":" + created + ",\"fields\":[\"" + _commentsField +
                  "\"],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(added.Success).IsTrue();

        var applied = tool.ExecuteForTests(
            Parse("{\"scheduleId\":" + created + ",\"filters\":[{\"field\":\"" + _commentsField +
                  "\",\"op\":\"contains\",\"value\":\"SchWrite\"}],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(applied.Success).IsTrue();

        var reader = new GetScheduleDefinitionTool(new StubRevitContext(Doc));
        var definition = reader.ExecuteForTests(
            Doc,
            Parse("{\"scheduleId\":" + created + "}"),
            CancellationToken.None);
        var data = definition.Data!.Value;
        await Assert.That(data.GetProperty("filterCount").GetInt32()).IsEqualTo(1);
        await Assert.That(data.GetProperty("filters")[0].GetProperty("op").GetString())
            .IsEqualTo("Contains");
        await Assert.That(data.GetProperty("filters")[0].GetProperty("value").GetString())
            .IsEqualTo("SchWrite");

        var undo = UndoLast();
        await Assert.That(undo.Success).IsTrue();
        var afterUndo = reader.ExecuteForTests(
            Doc,
            Parse("{\"scheduleId\":" + created + "}"),
            CancellationToken.None);
        await Assert.That(afterUndo.Data!.Value.GetProperty("filterCount").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task SetScheduleFilter_DryRun_EstimatesRowsAfterWithSampleNames()
    {
        if (_wallCount == 0)
            Skip.Test("В шаблоне проекта нет типа стены");

        var created = CreateWallSchedule();
        var addField = CreateTool<AddScheduleFieldTool>();
        await Assert.That(
            addField.ExecuteForTests(
                Parse("{\"scheduleId\":" + created + ",\"fields\":[\"" + _commentsField +
                      "\"],\"dryRun\":false}"),
                approved: true,
                CancellationToken.None).Success).IsTrue();

        var tool = CreateTool<SetScheduleFilterTool>();
        var dry = tool.ExecuteForTests(
            Parse("{\"scheduleId\":" + created + ",\"filters\":[{\"field\":\"" + _commentsField +
                  "\",\"op\":\"equals\",\"value\":\"" + WallComment + "\"}],\"dryRun\":true}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(dry.Success).IsTrue();
        var data = dry.Data!.Value;
        await Assert.That(data.GetProperty("estimatedRowsAfter").GetInt32()).IsEqualTo(_wallCount);
        var sample = data.GetProperty("estimatedSampleNames").EnumerateArray().ToList();
        await Assert.That(sample.Count).IsEqualTo(Math.Min(5, _wallCount));

        var none = tool.ExecuteForTests(
            Parse("{\"scheduleId\":" + created + ",\"filters\":[{\"field\":\"" + _commentsField +
                  "\",\"op\":\"equals\",\"value\":\"NoSuchCommentValue\"}],\"dryRun\":true}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(none.Success).IsTrue();
        await Assert.That(none.Data!.Value.GetProperty("estimatedRowsAfter").GetInt32())
            .IsEqualTo(0);
        await Assert.That(none.Data.Value.GetProperty("estimatedSampleNames").GetArrayLength())
            .IsEqualTo(0);

        // The estimate ran inside the rolled-back dry-run transaction: the
        // schedule itself must be untouched.
        var reader = new GetScheduleDefinitionTool(new StubRevitContext(Doc));
        var definition = reader.ExecuteForTests(
            Doc,
            Parse("{\"scheduleId\":" + created + "}"),
            CancellationToken.None);
        await Assert.That(definition.Data!.Value.GetProperty("filterCount").GetInt32())
            .IsEqualTo(0);
    }

    [Test]
    public async Task SetScheduleSort_Applies_ReadsBack_AndUndoRestores()
    {
        var created = CreateWallSchedule();
        var addField = CreateTool<AddScheduleFieldTool>();
        await Assert.That(
            addField.ExecuteForTests(
                Parse("{\"scheduleId\":" + created + ",\"fields\":[\"" + _commentsField +
                      "\"],\"dryRun\":false}"),
                approved: true,
                CancellationToken.None).Success).IsTrue();

        var tool = CreateTool<SetScheduleSortTool>();
        var result = tool.ExecuteForTests(
            Parse("{\"scheduleId\":" + created + ",\"sortGroups\":[{\"field\":\"" +
                  _commentsField + "\",\"order\":\"desc\"}],\"itemized\":false,\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();

        var reader = new GetScheduleDefinitionTool(new StubRevitContext(Doc));
        var definition = reader.ExecuteForTests(
            Doc,
            Parse("{\"scheduleId\":" + created + "}"),
            CancellationToken.None);
        var data = definition.Data!.Value;
        await Assert.That(data.GetProperty("isItemized").GetBoolean()).IsFalse();
        await Assert.That(data.GetProperty("sortGroupFieldCount").GetInt32()).IsEqualTo(1);
        await Assert.That(data.GetProperty("sortGroupFields")[0].GetProperty("order").GetString())
            .IsEqualTo("Descending");

        var undo = UndoLast();
        await Assert.That(undo.Success).IsTrue();
        var restored = reader.ExecuteForTests(
            Doc,
            Parse("{\"scheduleId\":" + created + "}"),
            CancellationToken.None);
        var restoredData = restored.Data!.Value;
        await Assert.That(restoredData.GetProperty("sortGroupFieldCount").GetInt32())
            .IsEqualTo(0);
        await Assert.That(restoredData.GetProperty("isItemized").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task PlaceScheduleOnSheet_Applies_ReadsBack_Duplicate_NoChanges_UndoDeletes()
    {
        if (!_sheetCreated)
            Skip.Test("В шаблоне проекта нет Title Block — лист не создать");

        var created = CreateWallSchedule();
        var tool = CreateTool<PlaceScheduleOnSheetTool>();
        var args = "{\"scheduleId\":" + created + ",\"sheetNumber\":\"" + SheetNumber +
                   "\",\"pointMmX\":120,\"pointMmY\":80,\"dryRun\":false}";
        var result = tool.ExecuteForTests(
            Parse(args), approved: true, CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(Math.Abs(result.Data!.Value.GetProperty("actualMmX").GetDouble() - 120) < 0.5)
            .IsTrue();
        await Assert.That(Math.Abs(result.Data.Value.GetProperty("actualMmY").GetDouble() - 80) < 0.5)
            .IsTrue();

        var content = new GetSheetContentTool(new StubRevitContext(Doc));
        var sheetContent = content.ExecuteForTests(
            Doc,
            Parse("{\"number\":\"" + SheetNumber + "\"}"),
            CancellationToken.None);
        await Assert.That(sheetContent.Success).IsTrue();
        var schedules = sheetContent.Data!.Value.GetProperty("schedules").EnumerateArray()
            .Where(item => item.GetProperty("scheduleId").GetInt64() == created)
            .ToList();
        await Assert.That(schedules.Count).IsEqualTo(1);
        await Assert.That(
            Math.Abs(schedules[0].GetProperty("xMm").GetDouble() - 120) < 0.5).IsTrue();
        await Assert.That(
            Math.Abs(schedules[0].GetProperty("yMm").GetDouble() - 80) < 0.5).IsTrue();

        var duplicate = tool.ExecuteForTests(
            Parse(args), approved: true, CancellationToken.None);
        await Assert.That(duplicate.Success).IsTrue();
        await Assert.That(duplicate.Data!.Value.GetProperty("noChanges").GetBoolean()).IsTrue();

        var undo = UndoLast();
        await Assert.That(undo.Success).IsTrue();
        var afterUndo = content.ExecuteForTests(
            Doc,
            Parse("{\"number\":\"" + SheetNumber + "\"}"),
            CancellationToken.None);
        await Assert.That(
            afterUndo.Data!.Value.GetProperty("schedules").EnumerateArray()
                .Count(item => item.GetProperty("scheduleId").GetInt64() == created)).IsEqualTo(0);
        await Assert.That(Doc.GetElement(ElementIdCompat.Create(created))).IsNotNull();
    }

    [Test]
    public async Task UndoLast_AfterCreateSchedule_DeletesSchedule()
    {
        var created = CreateWallSchedule();
        await Assert.That(FindSchedule(ScheduleName)).IsNotNull();

        var undo = UndoLast();

        await Assert.That(undo.Success).IsTrue();
        await Assert.That(FindSchedule(ScheduleName)).IsNull();
        await Assert.That(Doc.GetElement(ElementIdCompat.Create(created))).IsNull();
        await Assert.That(_changeSets!.Last!.Header.AppliedAt).IsNull();
    }

    [Test]
    public async Task Undo_RestoresNonEmptyFilterSnapshot()
    {
        var created = CreateWallSchedule();
        var addField = CreateTool<AddScheduleFieldTool>();
        await Assert.That(
            addField.ExecuteForTests(
                Parse("{\"scheduleId\":" + created + ",\"fields\":[\"" + _commentsField +
                      "\"],\"dryRun\":false}"),
                approved: true,
                CancellationToken.None).Success).IsTrue();

        var tool = CreateTool<SetScheduleFilterTool>();
        var filterA = "{\"field\":\"" + _commentsField + "\",\"op\":\"equals\",\"value\":\"" +
                      WallComment + "\"}";
        var filterB = "{\"field\":\"" + _commentsField + "\",\"op\":\"contains\",\"value\":\"NoSuchValue\"}";
        var first = tool.ExecuteForTests(
            Parse("{\"scheduleId\":" + created + ",\"filters\":[" + filterA + "],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(first.Success).IsTrue();

        var second = tool.ExecuteForTests(
            Parse("{\"scheduleId\":" + created + ",\"filters\":[" + filterB + "],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(second.Success).IsTrue();

        var undo = UndoLast();
        await Assert.That(undo.Success).IsTrue();

        var reader = new GetScheduleDefinitionTool(new StubRevitContext(Doc));
        var afterUndo = reader.ExecuteForTests(
            Doc,
            Parse("{\"scheduleId\":" + created + "}"),
            CancellationToken.None);
        var data = afterUndo.Data!.Value;
        await Assert.That(data.GetProperty("filterCount").GetInt32()).IsEqualTo(1);
        var restored = data.GetProperty("filters")[0];
        await Assert.That(restored.GetProperty("op").GetString()).IsEqualTo("Equal");
        await Assert.That(restored.GetProperty("value").GetString()).IsEqualTo(WallComment);
    }

    [Test]
    public async Task RemoveScheduleField_Applies_ReadsBack_AndUndoReaddsField()
    {
        var created = CreateWallSchedule();
        var addField = CreateTool<AddScheduleFieldTool>();
        await Assert.That(
            addField.ExecuteForTests(
                Parse("{\"scheduleId\":" + created + ",\"fields\":[\"" + _commentsField +
                      "\",\"" + _markField + "\"],\"dryRun\":false}"),
                approved: true,
                CancellationToken.None).Success).IsTrue();

        var tool = CreateTool<RemoveScheduleFieldTool>();
        var result = tool.ExecuteForTests(
            Parse("{\"scheduleId\":" + created + ",\"fields\":[\"" + _commentsField +
                  "\"],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var fields = result.Data!.Value.GetProperty("fields").EnumerateArray()
            .Select(value => value.GetString())
            .ToList();
        await Assert.That(fields.Contains(_commentsField)).IsFalse();
        await Assert.That(fields.Contains(_markField)).IsTrue();

        var unknown = tool.ExecuteForTests(
            Parse("{\"scheduleId\":" + created + ",\"fields\":[\"Нет такого поля\"],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(unknown.Success).IsTrue();
        await Assert.That(unknown.Data!.Value.GetProperty("noChanges").GetBoolean()).IsTrue();

        var undo = UndoLast();
        await Assert.That(undo.Success).IsTrue();
        var schedule = (ViewSchedule)Doc.GetElement(ElementIdCompat.Create(created))!;
        var remaining = AgentScheduleReadFieldNames(schedule);
        await Assert.That(remaining.Contains(_commentsField)).IsTrue();
    }

    [Test]
    public async Task SetScheduleFieldOrder_Applies_ReadsBack_Mismatch_AndUndoRestores()
    {
        var created = CreateWallSchedule();
        var addField = CreateTool<AddScheduleFieldTool>();
        await Assert.That(
            addField.ExecuteForTests(
                Parse("{\"scheduleId\":" + created + ",\"fields\":[\"" + _commentsField +
                      "\",\"" + _markField + "\"],\"dryRun\":false}"),
                approved: true,
                CancellationToken.None).Success).IsTrue();

        var schedule = (ViewSchedule)Doc.GetElement(ElementIdCompat.Create(created))!;
        var actual = AgentScheduleReadFieldNames(schedule);
        if (actual.Count < 2 || actual.Any(name => name.Contains('"')))
            Skip.Test("Шаблон дал неожидаемые имена полей: " + string.Join("|", actual));

        var tool = CreateTool<SetScheduleFieldOrderTool>();
        var mismatch = tool.ExecuteForTests(
            Parse("{\"scheduleId\":" + created + ",\"fields\":[\"" + actual[0] +
                  "\"],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(mismatch.Success).IsFalse();
        await Assert.That(mismatch.ErrorCode).IsEqualTo("order_mismatch");

        var reversed = Enumerable.Reverse(actual).ToList();
        var result = tool.ExecuteForTests(
            Parse("{\"scheduleId\":" + created + ",\"fields\":[\"" + reversed[0] +
                  "\",\"" + reversed[1] + "\"],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(result.Success).IsTrue();
        var order = result.Data!.Value.GetProperty("fields").EnumerateArray()
            .Select(value => value.GetString())
            .ToList();
        await Assert.That(order.Count).IsEqualTo(2);
        await Assert.That(string.Join("|", order)).IsEqualTo(string.Join("|", reversed));

        var undo = UndoLast();
        await Assert.That(undo.Success).IsTrue();
        var restored = AgentScheduleReadFieldNames(
            (ViewSchedule)Doc.GetElement(ElementIdCompat.Create(created))!);
        await Assert.That(string.Join("|", restored)).IsEqualTo(string.Join("|", actual));
    }

    [Test]
    public async Task SetScheduleFieldVisibility_Applies_ReadsBack_AndUndoRestores()
    {
        var created = CreateWallSchedule();
        var addField = CreateTool<AddScheduleFieldTool>();
        await Assert.That(
            addField.ExecuteForTests(
                Parse("{\"scheduleId\":" + created + ",\"fields\":[\"" + _commentsField +
                      "\"],\"dryRun\":false}"),
                approved: true,
                CancellationToken.None).Success).IsTrue();

        var tool = CreateTool<SetScheduleFieldVisibilityTool>();
        var result = tool.ExecuteForTests(
            Parse("{\"scheduleId\":" + created + ",\"fields\":[{\"name\":\"" + _commentsField +
                  "\",\"hidden\":true}],\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        var states = result.Data!.Value.GetProperty("fields").EnumerateArray().ToList();
        var hiddenState = states.First(state =>
            string.Equals(state.GetProperty("name").GetString(), _commentsField,
                StringComparison.OrdinalIgnoreCase));
        await Assert.That(hiddenState.GetProperty("hidden").GetBoolean()).IsTrue();

        var undo = UndoLast();
        await Assert.That(undo.Success).IsTrue();
        var schedule = (ViewSchedule)Doc.GetElement(ElementIdCompat.Create(created))!;
        foreach (var fieldId in schedule.Definition.GetFieldOrder())
        {
            var field = schedule.Definition.GetField(fieldId);
            if (string.Equals(field.GetName(), _commentsField, StringComparison.OrdinalIgnoreCase))
                await Assert.That(field.IsHidden).IsFalse();
        }
    }

    [Test]
    public async Task SetScheduleTotals_Applies_ReadsBack_AndUndoRestores()
    {
        var created = CreateWallSchedule();
        var lengthField = FindSchedulableFieldName(
            (ViewSchedule)Doc.GetElement(ElementIdCompat.Create(created))!,
            BuiltInParameter.CURVE_ELEM_LENGTH);
        if (lengthField is null)
            Skip.Test("В шаблоне нет поля длины для стен");

        var addField = CreateTool<AddScheduleFieldTool>();
        await Assert.That(
            addField.ExecuteForTests(
                Parse("{\"scheduleId\":" + created + ",\"fields\":[\"" + lengthField +
                      "\"],\"dryRun\":false}"),
                approved: true,
                CancellationToken.None).Success).IsTrue();

        var columnField = ActualFieldName(
            (ViewSchedule)Doc.GetElement(ElementIdCompat.Create(created))!,
            BuiltInParameter.CURVE_ELEM_LENGTH);
        if (columnField is null)
            Skip.Test("Поле длины не стало колонкой спецификации");

        var tool = CreateTool<SetScheduleTotalsTool>();
        var result = tool.ExecuteForTests(
            Parse("{\"scheduleId\":" + created + ",\"fields\":[{\"name\":\"" + columnField +
                  "\",\"mode\":\"totals\"}],\"grandTotal\":true,\"grandTotalCount\":true," +
                  "\"grandTotalTitle\":\"Всего\",\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        if (!result.Success)
            Assert.Fail("set_schedule_totals failed: " + result.ErrorCode + " — " +
                        result.Message);

        await Assert.That(result.Success).IsTrue();
        var totals = result.Data!.Value.GetProperty("totals");
        await Assert.That(totals.GetProperty("grandTotal").GetBoolean()).IsTrue();
        await Assert.That(totals.GetProperty("grandTotalCount").GetBoolean()).IsTrue();
        await Assert.That(totals.GetProperty("grandTotalTitle").GetString()).IsEqualTo("Всего");
        var modes = totals.GetProperty("fields").EnumerateArray()
            .Where(state => string.Equals(state.GetProperty("name").GetString(), columnField,
                StringComparison.OrdinalIgnoreCase))
            .ToList();
        await Assert.That(modes.Count).IsEqualTo(1);
        await Assert.That(modes[0].GetProperty("mode").GetString()).IsEqualTo("totals");

        var afterApplyDef = ((ViewSchedule)Doc.GetElement(
            ElementIdCompat.Create(created))!).Definition;
        if (!afterApplyDef.ShowGrandTotal)
            Assert.Fail("apply did not stick: after commit ShowGrandTotal=" +
                        afterApplyDef.ShowGrandTotal);

        var undo = UndoLast();
        if (!undo.Success)
        {
            var failed = undo.Data!.Value.GetProperty("failed");
            Assert.Fail("undo of totals failed: " + undo.ErrorCode + " — " + undo.Message +
                        " | failed=" + JsonSerializer.Serialize(failed));
        }
        await Assert.That(undo.Success).IsTrue();
        var schedule = (ViewSchedule)Doc.GetElement(ElementIdCompat.Create(created))!;
        await Assert.That(schedule.Definition.ShowGrandTotal).IsFalse();
        foreach (var fieldId in schedule.Definition.GetFieldOrder())
        {
            var field = schedule.Definition.GetField(fieldId);
            if (string.Equals(field.GetName(), columnField, StringComparison.OrdinalIgnoreCase))
                await Assert.That(field.DisplayType).IsEqualTo(ScheduleFieldDisplayType.Standard);
        }
    }

    private static string? ActualFieldName(ViewSchedule schedule, BuiltInParameter parameter)
    {
        foreach (var fieldId in schedule.Definition.GetFieldOrder())
        {
            var field = schedule.Definition.GetField(fieldId);
            if (field.ParameterId == new ElementId(parameter))
                return field.GetName();
        }

        return null;
    }

    [Test]
    public async Task MoveScheduleOnSheet_Applies_ReadsBack_NotPlaced_AndUndoRestores()
    {
        if (!_sheetCreated)
            Skip.Test("В шаблоне проекта нет Title Block — лист не создать");

        var created = CreateWallSchedule();
        var place = CreateTool<PlaceScheduleOnSheetTool>();
        var placed = place.ExecuteForTests(
            Parse("{\"scheduleId\":" + created + ",\"sheetNumber\":\"" + SheetNumber +
                  "\",\"pointMmX\":120,\"pointMmY\":80,\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(placed.Success).IsTrue();

        var notPlaced = CreateTool<MoveScheduleOnSheetTool>().ExecuteForTests(
            Parse("{\"scheduleId\":" + created + ",\"sheetNumber\":\"SC-SW-999\"," +
                  "\"pointMmX\":10,\"pointMmY\":10,\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        await Assert.That(notPlaced.Success).IsFalse();
        await Assert.That(notPlaced.ErrorCode).IsEqualTo("sheet_not_found");

        var tool = CreateTool<MoveScheduleOnSheetTool>();
        var result = tool.ExecuteForTests(
            Parse("{\"scheduleId\":" + created + ",\"sheetNumber\":\"" + SheetNumber +
                  "\",\"pointMmX\":200,\"pointMmY\":150,\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(
            Math.Abs(result.Data!.Value.GetProperty("actualMmX").GetDouble() - 200) < 0.5)
            .IsTrue();
        await Assert.That(
            Math.Abs(result.Data.Value.GetProperty("actualMmY").GetDouble() - 150) < 0.5)
            .IsTrue();

        var content = new GetSheetContentTool(new StubRevitContext(Doc));
        var sheetContent = content.ExecuteForTests(
            Doc,
            Parse("{\"number\":\"" + SheetNumber + "\"}"),
            CancellationToken.None);
        var placement = sheetContent.Data!.Value.GetProperty("schedules").EnumerateArray()
            .First(item => item.GetProperty("scheduleId").GetInt64() == created);
        await Assert.That(Math.Abs(placement.GetProperty("xMm").GetDouble() - 200) < 0.5).IsTrue();
        await Assert.That(Math.Abs(placement.GetProperty("yMm").GetDouble() - 150) < 0.5).IsTrue();

        var undo = UndoLast();
        await Assert.That(undo.Success).IsTrue();
        var afterUndo = content.ExecuteForTests(
            Doc,
            Parse("{\"number\":\"" + SheetNumber + "\"}"),
            CancellationToken.None);
        var restored = afterUndo.Data!.Value.GetProperty("schedules").EnumerateArray()
            .First(item => item.GetProperty("scheduleId").GetInt64() == created);
        await Assert.That(Math.Abs(restored.GetProperty("xMm").GetDouble() - 120) < 0.5).IsTrue();
        await Assert.That(Math.Abs(restored.GetProperty("yMm").GetDouble() - 80) < 0.5).IsTrue();
    }

    private static string? FindSchedulableFieldName(
        ViewSchedule schedule,
        BuiltInParameter parameter)
    {
        foreach (var field in schedule.Definition.GetSchedulableFields())
        {
            if (field.ParameterId == new ElementId(parameter))
                return field.GetName(schedule.Document);
        }

        return null;
    }

    private long CreateWallSchedule()
    {
        var tool = CreateTool<CreateScheduleTool>();
        var result = tool.ExecuteForTests(
            Parse("{\"category\":\"OST_Walls\",\"name\":\"" + ScheduleName + "\",\"dryRun\":false}"),
            approved: true,
            CancellationToken.None);
        if (!result.Success)
            throw new InvalidOperationException(
                "Сид не смог создать спецификацию: " + result.ErrorCode);

        return result.Data!.Value.GetProperty("created").GetProperty("scheduleId").GetInt64();
    }

    private ToolResult UndoLast()
    {
        var undoTool = new UndoLastTool(new StubRevitContext(Doc), _transactions!, _changeSets!);
        return undoTool.ExecuteForTests(
            Parse("""{"dryRun":false}"""),
            approved: true,
            CancellationToken.None);
    }

    private static List<string> AgentScheduleReadFieldNames(ViewSchedule schedule)
    {
        var names = new List<string>();
        var definition = schedule.Definition;
        foreach (var fieldId in definition.GetFieldOrder())
        {
            try
            {
                names.Add(definition.GetField(fieldId).GetName());
            }
            catch (Exception)
            {
                // Skip unreadable fields.
            }
        }

        return names;
    }

    private ViewSchedule? FindSchedule(string name)
    {
        foreach (var element in new FilteredElementCollector(Doc).OfClass(typeof(ViewSchedule)))
        {
            if (element is ViewSchedule schedule &&
                string.Equals(schedule.Name, name, StringComparison.Ordinal))
                return schedule;
        }

        return null;
    }

    private T CreateTool<T>()
        where T : AgentWriteToolBase
    {
        var context = new StubRevitContext(Doc);
        return typeof(T).Name switch
        {
            nameof(CreateScheduleTool) => (T)(object)new CreateScheduleTool(
                context, _transactions!, _changeSets!),
            nameof(AddScheduleFieldTool) => (T)(object)new AddScheduleFieldTool(
                context, _transactions!, _changeSets!),
            nameof(SetScheduleFilterTool) => (T)(object)new SetScheduleFilterTool(
                context, _transactions!, _changeSets!),
            nameof(SetScheduleSortTool) => (T)(object)new SetScheduleSortTool(
                context, _transactions!, _changeSets!),
            nameof(PlaceScheduleOnSheetTool) => (T)(object)new PlaceScheduleOnSheetTool(
                context, _transactions!, _changeSets!),
            nameof(RemoveScheduleFieldTool) => (T)(object)new RemoveScheduleFieldTool(
                context, _transactions!, _changeSets!),
            nameof(SetScheduleFieldOrderTool) => (T)(object)new SetScheduleFieldOrderTool(
                context, _transactions!, _changeSets!),
            nameof(SetScheduleFieldVisibilityTool) => (T)(object)new SetScheduleFieldVisibilityTool(
                context, _transactions!, _changeSets!),
            nameof(SetScheduleTotalsTool) => (T)(object)new SetScheduleTotalsTool(
                context, _transactions!, _changeSets!),
            nameof(MoveScheduleOnSheetTool) => (T)(object)new MoveScheduleOnSheetTool(
                context, _transactions!, _changeSets!),
            _ => throw new ArgumentOutOfRangeException(nameof(T), typeof(T).Name)
        };
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
